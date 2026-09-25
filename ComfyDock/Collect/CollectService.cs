using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyDock.Models;
using ComfyDock.Shortcut;
using ComfyDock.Store;

namespace ComfyDock.Collect;

/// <summary>
/// 采集编排——单行刷新与全量采集共用一条串行通道；**每采完一行立即落盘**，
/// 面板轮询即可看到逐行更新。
/// </summary>
public sealed class CollectService
{
    /// <summary>运行选项。</summary>
    private readonly AppOptions _options;

    /// <summary>单槽位采集器。</summary>
    private readonly Collector _collector;

    /// <summary>快照存储。</summary>
    private readonly AccountStore _store;

    /// <summary>串行任务队列（采集 / 删除 / 上传共用）。</summary>
    private readonly TaskQueue _tasks = new TaskQueue();

    /// <summary>快照与进度读写锁。</summary>
    private readonly object _gate = new object();

    /// <summary>当前账号快照。</summary>
    private AccountSnapshot _snapshot;

    /// <summary>当前采集进度。</summary>
    private CollectProgress _progress = new CollectProgress();

    /// <summary>构造采集服务（启动即读本地快照）。</summary>
    /// <param name="options">运行选项。</param>
    /// <param name="collector">单槽位采集器。</param>
    /// <param name="store">快照存储。</param>
    public CollectService(AppOptions options, Collector collector, AccountStore store)
    {
        _options = options;
        _collector = collector;
        _store = store;
        _snapshot = store.Load();
    }

    /// <summary>任务队列（面板据此显示排队与结果）。</summary>
    public TaskQueue Tasks
    {
        get
        {
            return _tasks;
        }
    }

    /// <summary>当前快照（浅拷贝——行对象整体替换，不原地改）。</summary>
    public AccountSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new AccountSnapshot
                {
                    GeneratedAt = _snapshot.GeneratedAt,
                    Accounts = new List<AccountSlot>(_snapshot.Accounts),
                };
            }
        }
    }

    /// <summary>当前进度快照。</summary>
    public CollectProgress Progress
    {
        get
        {
            lock (_gate)
            {
                return _progress.Clone();
            }
        }
    }
    /// <summary>从快照里摘掉一个工作流——删除任务成功后调用；列表立即反映结果，不触发采集。</summary>
    /// <param name="shortcutPath">快捷方式完整路径。</param>
    /// <param name="workflowId">工作流 id。</param>
    /// <returns>是否真的摘掉了条目。</returns>
    public bool RemoveWorkflow(string shortcutPath, string workflowId)
    {
        lock (_gate)
        {
            int index = _snapshot.Accounts.FindIndex(item =>
                string.Equals(item.ShortcutPath, shortcutPath, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                return false;
            }

            AccountSlot slot = _snapshot.Accounts[index];
            List<WorkflowItem> remaining = new List<WorkflowItem>();
            foreach (WorkflowItem item in slot.Workflows)
            {
                if (!string.Equals(item.WorkflowId, workflowId, StringComparison.Ordinal))
                {
                    remaining.Add(item);
                }
            }

            if (remaining.Count == slot.Workflows.Count)
            {
                return false;
            }

            _snapshot.Accounts[index] = new AccountSlot
            {
                ShortcutName = slot.ShortcutName,
                ShortcutPath = slot.ShortcutPath,
                ProfileDir = slot.ProfileDir,
                Status = slot.Status,
                UserId = slot.UserId,
                NickName = slot.NickName,
                Mobile = slot.Mobile,
                ApiKey = slot.ApiKey,
                TotalCoin = slot.TotalCoin,
                CoinGainTime = slot.CoinGainTime,
                CoinGainAmount = slot.CoinGainAmount,
                CoinRefreshTime = slot.CoinRefreshTime,
                CoinSpent = slot.CoinSpent,
                CollectedAt = slot.CollectedAt,
                Error = slot.Error,
                Workflows = remaining,
                WorkflowError = slot.WorkflowError,
            };

            _snapshot.GeneratedAt = Clock.Now();
            _store.Save(_snapshot);
            return true;
        }
    }

    /// <summary>全量采集入队——等价于把所有行依次「该行刷新」一遍。</summary>
    /// <returns>任务记录。</returns>
    public TaskRecord EnqueueAll()
    {
        return _tasks.Enqueue("collect-all", "全量采集", "*", RunAllAsync);
    }

    /// <summary>单行刷新入队——采完立即落盘。</summary>
    /// <param name="shortcutPath">快捷方式完整路径。</param>
    /// <param name="error">未入队的原因。</param>
    /// <returns>任务记录；快捷方式不在扫描目录内时返回 null。</returns>
    public TaskRecord? EnqueueOne(string shortcutPath, out string error)
    {
        ShortcutEntry? target = ShortcutScanner.Scan(_options.GoogleRoot)
            .FirstOrDefault(entry => string.Equals(entry.ShortcutPath, shortcutPath, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            error = "该快捷方式不在扫描目录内：" + shortcutPath;
            return null;
        }

        error = "";
        return _tasks.Enqueue("collect-one", "刷新 " + target.ShortcutName, target.ShortcutPath, () => RunOneAsync(target));
    }

    /// <summary>全量采集任务体——逐行采集并推进进度。</summary>
    /// <returns>任务结果。</returns>
    private async Task<TaskOutcome> RunAllAsync()
    {
        List<ShortcutEntry> entries = ShortcutScanner.Scan(_options.GoogleRoot);

        // [段1] 开进度——面板据此显示 x/y
        lock (_gate)
        {
            _progress = new CollectProgress
            {
                Running = true,
                Kind = "all",
                Total = entries.Count,
                Done = 0,
                Current = "",
                StartedAt = Clock.Now(),
                FinishedAt = "",
            };
            EnsureSlots(entries);
        }

        // [段2] 逐行采集——并发度由 --concurrency 决定（默认 2）
        var gate = new SemaphoreSlim(_options.Concurrency);
        var runs = new List<Task>();
        foreach (ShortcutEntry entry in entries)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            runs.Add(RunOneParallelAsync(entry, gate));
        }

        await Task.WhenAll(runs).ConfigureAwait(false);

        // [段3] 收进度
        int done;
        lock (_gate)
        {
            done = _progress.Done;
            _progress.Running = false;
            _progress.Current = "";
            _progress.FinishedAt = Clock.Now();
        }

        return new TaskOutcome { Ok = true, Message = "全量采集完成 " + done + "/" + entries.Count };
    }

    /// <summary>单行刷新任务体——采一行并报余额。</summary>
    /// <param name="entry">账号槽位。</param>
    /// <returns>任务结果。</returns>
    private async Task<TaskOutcome> RunOneAsync(ShortcutEntry entry)
    {
        lock (_gate)
        {
            _progress = new CollectProgress
            {
                Running = true,
                Kind = "one",
                Total = 1,
                Done = 0,
                Current = entry.ShortcutName,
                StartedAt = Clock.Now(),
                FinishedAt = "",
            };
        }

        CollectResult outcome = await CollectOneCoreAsync(entry).ConfigureAwait(false);

        lock (_gate)
        {
            _progress.Done = 1;
            _progress.Running = false;
            _progress.Current = "";
            _progress.FinishedAt = Clock.Now();
        }

        if (string.Equals(outcome.Status, "ok", StringComparison.Ordinal))
        {
            return new TaskOutcome
            {
                Ok = true,
                Message = "已刷新 " + entry.ShortcutName + " · 余额 " + (outcome.TotalCoin.HasValue ? outcome.TotalCoin.Value.ToString() : "—"),
            };
        }

        return new TaskOutcome { Ok = false, Message = entry.ShortcutName + "：" + outcome.Error };
    }

    /// <summary>采集单行并落盘。</summary>
    /// <param name="entry">账号槽位。</param>
    /// <returns>采集结果。</returns>
    private async Task<CollectResult> CollectOneCoreAsync(ShortcutEntry entry)
    {
        CollectResult outcome;
        try
        {
            outcome = await _collector.CollectAsync(entry.ProfileDir, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            outcome = new CollectResult { Status = "error", Error = ex.Message };
        }

        Apply(entry, outcome);
        return outcome;
    }
    /// <summary>并行采集一行——完成后推进进度并归还并发额度。</summary>
    /// <param name="entry">账号槽位。</param>
    /// <param name="gate">并发闸门。</param>
    /// <returns>异步任务。</returns>
    private async Task RunOneParallelAsync(ShortcutEntry entry, SemaphoreSlim gate)
    {
        try
        {
            lock (_gate)
            {
                _progress.Current = entry.ShortcutName;
            }

            await CollectOneCoreAsync(entry).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _progress.Done = _progress.Done + 1;
            }

            gate.Release();
        }
    }

    private void Apply(ShortcutEntry entry, CollectResult outcome)
    {
        var slot = new AccountSlot
        {
            ShortcutName = entry.ShortcutName,
            ShortcutPath = entry.ShortcutPath,
            ProfileDir = entry.ProfileDir,
            Status = outcome.Status,
            UserId = outcome.UserId,
            NickName = outcome.NickName,
            Mobile = outcome.Mobile,
            ApiKey = outcome.ApiKey,
            TotalCoin = outcome.TotalCoin,
            CoinGainTime = outcome.CoinGainTime,
            CoinGainAmount = outcome.CoinGainAmount,
            CoinRefreshTime = outcome.CoinRefreshTime,
            CoinSpent = outcome.CoinSpent,
            CollectedAt = Clock.Now(),
            Error = outcome.Error,
            Workflows = outcome.Workflows,
            WorkflowError = outcome.WorkflowError,
        };

        lock (_gate)
        {
            int index = _snapshot.Accounts.FindIndex(item =>
                string.Equals(item.ShortcutPath, slot.ShortcutPath, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                _snapshot.Accounts[index] = slot;
            }
            else
            {
                _snapshot.Accounts.Add(slot);
            }

            _snapshot.GeneratedAt = Clock.Now();
            _store.Save(_snapshot);
        }
    }

    private void EnsureSlots(List<ShortcutEntry> entries)
    {
        bool changed = false;
        foreach (ShortcutEntry entry in entries)
        {
            bool exists = _snapshot.Accounts.Any(item =>
                string.Equals(item.ShortcutPath, entry.ShortcutPath, StringComparison.OrdinalIgnoreCase));

            if (!exists)
            {
                _snapshot.Accounts.Add(new AccountSlot
                {
                    ShortcutName = entry.ShortcutName,
                    ShortcutPath = entry.ShortcutPath,
                    ProfileDir = entry.ProfileDir,
                    Status = "unknown",
                    Error = "等待采集",
                });
                changed = true;
            }
        }

        if (changed)
        {
            _store.Save(_snapshot);
        }
    }
}

/// <summary>采集进度快照。</summary>
public sealed class CollectProgress
{
    /// <summary>是否正在采集。</summary>
    public bool Running { get; set; }

    /// <summary>本次任务类型：all（全量）/ one（单行）。</summary>
    public string Kind { get; set; } = "";

    /// <summary>本批槽位总数。</summary>
    public int Total { get; set; }

    /// <summary>已完成数。</summary>
    public int Done { get; set; }

    /// <summary>当前正在采集的快捷方式名。</summary>
    public string Current { get; set; } = "";

    /// <summary>开始时刻。</summary>
    public string StartedAt { get; set; } = "";

    /// <summary>结束时刻。</summary>
    public string FinishedAt { get; set; } = "";

    /// <summary>浅拷贝（跨线程读取用）。</summary>
    /// <returns>进度副本。</returns>
    public CollectProgress Clone()
    {
        return new CollectProgress
        {
            Running = Running,
            Kind = Kind,
            Total = Total,
            Done = Done,
            Current = Current,
            StartedAt = StartedAt,
            FinishedAt = FinishedAt,
        };
    }
}

/// <summary>时钟——统一时间文本格式。</summary>
public static class Clock
{
    /// <summary>当前时刻（yyyy-MM-dd HH:mm:ss）。</summary>
    /// <returns>格式化时间文本。</returns>
    public static string Now()
    {
        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }
}
