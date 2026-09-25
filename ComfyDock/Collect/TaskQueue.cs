using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Collect;

/// <summary>
/// 串行任务队列——采集 / 删除 / 上传一律排队执行：同一时刻只跑一个浏览器重操作，
/// 排队等待而不是直接拒绝，重复点击不再互相打断。
/// </summary>
public sealed class TaskQueue
{
    /// <summary>历史保留条数。</summary>
    private const int HistoryLimit = 20;

    /// <summary>串行信号量——同一时刻只跑一个任务。</summary>
    private readonly SemaphoreSlim _slot = new SemaphoreSlim(1, 1);

    /// <summary>队列读写锁。</summary>
    private readonly object _gate = new object();

    /// <summary>排队中的任务（按入队顺序）。</summary>
    private readonly List<TaskRecord> _waiting = new List<TaskRecord>();

    /// <summary>最近完成的任务（最新在前，上限 HistoryLimit）。</summary>
    private readonly List<TaskRecord> _history = new List<TaskRecord>();

    /// <summary>正在执行的任务（无则 null）。</summary>
    private TaskRecord? _running;

    /// <summary>任务号计数器。</summary>
    private int _nextId;

    /// <summary>入队一个任务——立即返回任务号，执行在后台串行进行。</summary>
    /// <param name="kind">任务类型（collect-all / collect-one / delete / upload）。</param>
    /// <param name="label">面板显示用的说明。</param>
    /// <param name="targetKey">关联对象（快捷方式路径；全量任务填 *）。</param>
    /// <param name="work">任务体。</param>
    /// <returns>任务记录（含任务号与排队状态）。</returns>
    public TaskRecord Enqueue(string kind, string label, string targetKey, Func<Task<TaskOutcome>> work)
    {
        var item = new TaskRecord
        {
            Id = 0,
            Kind = kind,
            Label = label,
            TargetKey = targetKey,
            State = "queued",
            Message = "",
            QueuedAt = Clock.Now(),
            StartedAt = "",
            FinishedAt = "",
        };

        lock (_gate)
        {
            _nextId = _nextId + 1;
            item.Id = _nextId;
            _waiting.Add(item);
        }

        _ = Task.Run(() => RunAsync(item, work));
        return item;
    }

    /// <summary>当前队列快照（运行中 / 排队中 / 最近完成）。</summary>
    /// <returns>队列快照。</returns>
    public TaskQueueSnapshot Snapshot()
    {
        lock (_gate)
        {
            var snapshot = new TaskQueueSnapshot();
            if (_running is not null)
            {
                snapshot.Running = _running.Clone();
            }

            foreach (TaskRecord item in _waiting)
            {
                snapshot.Waiting.Add(item.Clone());
            }

            foreach (TaskRecord item in _history)
            {
                snapshot.Recent.Add(item.Clone());
            }

            return snapshot;
        }
    }

    /// <summary>串行执行一个任务——跑完入历史并腾出信号量。</summary>
    private async Task RunAsync(TaskRecord item, Func<Task<TaskOutcome>> work)
    {
        await _slot.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                _waiting.Remove(item);
                _running = item;
                item.State = "running";
                item.StartedAt = Clock.Now();
            }

            // [段1] 跑任务体——异常一律收敛成失败记录，队列不被打断
            var outcome = new TaskOutcome();
            try
            {
                outcome = await work().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                outcome = new TaskOutcome { Ok = false, Message = ex.Message };
            }

            lock (_gate)
            {
                item.Message = outcome.Message;
                item.State = outcome.Ok ? "done" : "failed";
            }
        }
        finally
        {
            // [段2] 收尾——入历史、腾出信号量
            lock (_gate)
            {
                item.FinishedAt = Clock.Now();
                _running = null;
                _history.Insert(0, item);
                if (_history.Count > HistoryLimit)
                {
                    _history.RemoveRange(HistoryLimit, _history.Count - HistoryLimit);
                }
            }

            _slot.Release();
        }
    }
}

/// <summary>任务记录——面板据此显示「排队中 / 处理中 / 结果」。</summary>
public sealed class TaskRecord
{
    /// <summary>任务号（递增）。</summary>
    public int Id { get; set; }

    /// <summary>任务类型：collect-all / collect-one / delete / upload。</summary>
    public string Kind { get; set; } = "";

    /// <summary>面板显示用的说明。</summary>
    public string Label { get; set; } = "";

    /// <summary>关联对象（快捷方式路径；全量任务为 *）。</summary>
    public string TargetKey { get; set; } = "";

    /// <summary>状态：queued / running / done / failed。</summary>
    public string State { get; set; } = "queued";

    /// <summary>结果说明（完成后填充）。</summary>
    public string Message { get; set; } = "";

    /// <summary>入队时刻。</summary>
    public string QueuedAt { get; set; } = "";

    /// <summary>开始时刻。</summary>
    public string StartedAt { get; set; } = "";

    /// <summary>结束时刻。</summary>
    public string FinishedAt { get; set; } = "";

    /// <summary>浅拷贝（跨线程读取用）。</summary>
    /// <returns>任务记录副本。</returns>
    public TaskRecord Clone()
    {
        return new TaskRecord
        {
            Id = Id,
            Kind = Kind,
            Label = Label,
            TargetKey = TargetKey,
            State = State,
            Message = Message,
            QueuedAt = QueuedAt,
            StartedAt = StartedAt,
            FinishedAt = FinishedAt,
        };
    }
}

/// <summary>任务结果。</summary>
public sealed class TaskOutcome
{
    /// <summary>是否成功。</summary>
    public bool Ok { get; set; } = true;

    /// <summary>结果说明。</summary>
    public string Message { get; set; } = "";
}

/// <summary>队列快照。</summary>
public sealed class TaskQueueSnapshot
{
    /// <summary>正在执行的任务（无则 null）。</summary>
    public TaskRecord? Running { get; set; }

    /// <summary>排队中的任务（按入队顺序）。</summary>
    public List<TaskRecord> Waiting { get; set; } = new List<TaskRecord>();

    /// <summary>最近完成的任务（最新在前）。</summary>
    public List<TaskRecord> Recent { get; set; } = new List<TaskRecord>();
}
