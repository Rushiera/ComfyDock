using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ComfyDock.Collect;
using ComfyDock.Models;

namespace ComfyDock.RunningHub;

/// <summary>账号可用性一行（面板显示——**不含密钥**，红线）。</summary>
public sealed class RhAccountRow
{
    /// <summary>快捷方式名（账号显示名）。</summary>
    public string Account { get; set; } = "";

    /// <summary>平台昵称。</summary>
    public string NickName { get; set; } = "";

    /// <summary>采集状态（ok / not-logged-in / error / running / unknown）。</summary>
    public string Status { get; set; } = "";

    /// <summary>推测剩余（算力额度 − 自获取以来花费 − 本地新消耗；下限 0）；未知为 null。</summary>
    public double? Remain { get; set; }

    /// <summary>是否可用（已登录 + 密钥在位 + 推测剩余 ≥ 20）。</summary>
    public bool Usable { get; set; }

    /// <summary>不可用原因 / 备注。</summary>
    public string Note { get; set; } = "";
}

/// <summary>额度概览——合计推测剩余 + 最早刷新时刻（面板与外部健康检查共用）。</summary>
public sealed class RhSummary
{
    /// <summary>账号总数。</summary>
    public int Accounts { get; set; }

    /// <summary>取到算力明细的账号数（合计只含这些账号）。</summary>
    public int Known { get; set; }

    /// <summary>推测剩余合计（RH 币）。</summary>
    public double Remain { get; set; }

    /// <summary>最早刷新时刻（yyyy-MM-dd HH:mm:ss）；全无算力明细时为空串。</summary>
    public string ResetAt { get; set; } = "";

    /// <summary>距最早刷新的分钟数（负数 = 刷新时刻已过）。</summary>
    public int ResetInMinutes { get; set; }

    /// <summary>快照采集时刻（yyyy-MM-dd HH:mm:ss）。</summary>
    public string CollectedAt { get; set; } = "";
}

/// <summary>一条任务的面板视图。</summary>
public sealed class RhTaskView
{
    /// <summary>标识（音频完整路径）。</summary>
    public string Id { get; set; } = "";

    /// <summary>音频文件名。</summary>
    public string Name { get; set; } = "";

    /// <summary>任务号。</summary>
    public string TaskId { get; set; } = "";

    /// <summary>使用的账号（快捷方式名）。</summary>
    public string Account { get; set; } = "";

    /// <summary>状态。</summary>
    public string Status { get; set; } = "";

    /// <summary>消耗（RH 币）。</summary>
    public double Coins { get; set; }

    /// <summary>失败原因。</summary>
    public string Error { get; set; } = "";

    /// <summary>提交时刻。</summary>
    public string SubmittedAt { get; set; } = "";

    /// <summary>完成时刻。</summary>
    public string FinishedAt { get; set; } = "";

    /// <summary>产物文件名清单。</summary>
    public List<string> Artifacts { get; set; } = new List<string>();
}

/// <summary>线上识别状态全景（面板一次取全）。</summary>
public sealed class RhStatus
{
    /// <summary>产物落点目录。</summary>
    public string OutDir { get; set; } = "";

    /// <summary>当前配置（含工作区清单）。</summary>
    public RhConfig Config { get; set; } = new RhConfig();

    /// <summary>工作区视图清单（每块一块——音频清单 + 任务 + 工作流 id）。</summary>
    public List<RhWorkspaceView> Workspaces { get; set; } = new List<RhWorkspaceView>();

    /// <summary>账号可用性表（各工作区共用同一张账号池）。</summary>
    public List<RhAccountRow> Accounts { get; set; } = new List<RhAccountRow>();

    /// <summary>最近一次轮询时刻。</summary>
    public string LastPoll { get; set; } = "";

    /// <summary>最近事件（新→旧，面板可见的失败与进度）。</summary>
    public List<string> Events { get; set; } = new List<string>();
}

/// <summary>一个工作区的面板视图——折叠区里的一切。</summary>
public sealed class RhWorkspaceView
{
    /// <summary>工作区 id。</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名（如 QWEN3ASR1.7B）。</summary>
    public string Name { get; set; } = "";

    /// <summary>云端工作流 id（写死在配置里，只读展示）。</summary>
    public string WorkflowId { get; set; } = "";

    /// <summary>是否内置块（内置块不给删）。</summary>
    public bool BuiltIn { get; set; }

    /// <summary>工作区类型（asr / gen）——前端按它渲染不同的输入面。</summary>
    public string Kind { get; set; } = "";

    /// <summary>gen 工作区选中的图片完整路径。</summary>
    public string ImageFile { get; set; } = "";

    /// <summary>gen 工作区的提示词。</summary>
    public string Prompt { get; set; } = "";

    /// <summary>该工作区选中的音频文件。</summary>
    public string AudioFile { get; set; } = "";

    /// <summary>该工作区选中的音频目录。</summary>
    public string AudioDir { get; set; } = "";

    /// <summary>该工作区的目录监听自动推。</summary>
    public bool AutoPush { get; set; }

    /// <summary>该工作区的音频清单（未提交的排前）。</summary>
    public List<string> AudioFiles { get; set; } = new List<string>();

    /// <summary>该工作区的任务清单（最新在前）。</summary>
    public List<RhTaskView> Tasks { get; set; } = new List<RhTaskView>();
}

/// <summary>一次提交的结果。</summary>
public sealed class RhSubmitResult
{
    /// <summary>是否成功。</summary>
    public bool Ok { get; set; }

    /// <summary>标识（音频完整路径）。</summary>
    public string Id { get; set; } = "";

    /// <summary>任务号。</summary>
    public string TaskId { get; set; } = "";

    /// <summary>使用的账号。</summary>
    public string Account { get; set; } = "";

    /// <summary>是否复用已有任务（未重复花钱）。</summary>
    public bool Reused { get; set; }

    /// <summary>结果说明。</summary>
    public string Message { get; set; } = "";
}

/// <summary>线上识别编排——音频 → 上传 → 起工作流 → 轮询 → 两个 txt 落 `out\`。
/// 账号**复用 accounts.json**（不新增账号表）；额度判定用采集到的**推测剩余**（不是固定上限），**低于 20 即换号**。
/// 任务回传后刷新该账号快照——余额 / 花费与站点对齐。</summary>
public sealed class RhService
{
    /// <summary>任务清单保留条数。</summary>
    private const int TaskViewLimit = 50;

    /// <summary>事件日志保留条数。</summary>
    private const int EventLimit = 60;

    /// <summary>后台轮询间隔（秒）。</summary>
    private const int PollSeconds = 15;

    /// <summary>上传超时（秒）——音频体积大，给足。</summary>
    private const int UploadTimeoutSeconds = 900;

    /// <summary>普通 API 调用超时（秒）。</summary>
    private const int ApiTimeoutSeconds = 120;

    /// <summary>产物下载超时（秒）。</summary>
    private const int DownloadTimeoutSeconds = 600;
    /// <summary>挑选账号的最低推测剩余（RH 币）——低于此值即换号，不推到额度用尽。</summary>
    private const int MinRemainToPick = 20;

    /// <summary>认作音频的扩展名。</summary>
    private static readonly string[] AudioExtensions = new string[]
    {
        ".mp3", ".wav", ".m4a", ".flac", ".aac", ".ogg", ".opus", ".wma",
    };

    /// <summary>采集服务——账号与密钥的唯一来源。</summary>
    private readonly CollectService _collect;

    /// <summary>配置存储。</summary>
    private readonly RhConfigStore _configStore;

    /// <summary>账本存储。</summary>
    private readonly RhLedgerStore _ledgerStore;

    /// <summary>HTTP 客户端。</summary>
    private readonly RhClient _client = new RhClient();

    /// <summary>产物落点目录。</summary>
    private readonly string _outDir;

    /// <summary>状态读写锁。</summary>
    private readonly object _gate = new object();

    /// <summary>重操作串行闸门（提交 / 轮询 / 取消一次只跑一个）。</summary>
    private readonly SemaphoreSlim _workGate = new SemaphoreSlim(1, 1);

    /// <summary>后台循环取消源。</summary>
    private CancellationTokenSource? _loopCts;

    /// <summary>后台循环任务。</summary>
    private Task? _loopTask;

    /// <summary>当前配置。</summary>
    private RhConfig _config;

    /// <summary>当前账本。</summary>
    private RhLedger _ledger;

    /// <summary>最近事件（新→旧）。</summary>
    private readonly List<string> _events = new List<string>();

    /// <summary>以采集服务与落盘路径构造（启动即读配置与账本）。</summary>
    /// <param name="collect">采集服务（账号来源）。</param>
    /// <param name="configPath">配置文件完整路径。</param>
    /// <param name="ledgerPath">账本文件完整路径。</param>
    /// <param name="outDir">产物落点目录。</param>
    public RhService(CollectService collect, string configPath, string ledgerPath, string outDir)
    {
        _collect = collect;
        _configStore = new RhConfigStore(configPath);
        _ledgerStore = new RhLedgerStore(ledgerPath);
        _outDir = outDir;
        _config = _configStore.Load();
        _ledger = _ledgerStore.Load();
        MigrateLegacyTasks();
    }

    /// <summary>产物落点目录。</summary>
    public string OutDir
    {
        get { return _outDir; }
    }
    /// <summary>
    /// 旧账本迁移——工作区化之前记的任务没有 WorkspaceId、标识是裸音频路径；
    /// 统一归到内置工作区并重写标识键（历史不清空，只是换个落位）。
    /// </summary>
    private void MigrateLegacyTasks()
    {
        string seedId = "";
        foreach (RhWorkspace workspace in _config.Workspaces)
        {
            if (workspace.BuiltIn && workspace.Kind == RhWorkspace.KindAsr)
            {
                seedId = workspace.Id;
                break;
            }
        }

        if (seedId.Length == 0 && _config.Workspaces.Count > 0)
        {
            seedId = _config.Workspaces[0].Id;
        }

        if (seedId.Length == 0)
        {
            return;
        }

        bool dirty = false;
        var migrated = new Dictionary<string, RhTaskEntry>();
        foreach (string ident in _ledger.Tasks.Keys)
        {
            RhTaskEntry entry = _ledger.Tasks[ident];
            if (entry.WorkspaceId.Length == 0)
            {
                entry.WorkspaceId = seedId;
                dirty = true;
            }

            string key = IdentOf(entry.WorkspaceId, entry.File);
            if (!string.Equals(key, ident, StringComparison.Ordinal))
            {
                entry.Id = key;
                dirty = true;
            }

            migrated[key] = entry;
        }

        if (!dirty)
        {
            return;
        }

        _ledger.Tasks = migrated;
        _ledgerStore.Save(_ledger);
        Log("旧账本已迁移到工作区「" + seedId + "」（" + migrated.Count + " 条任务）");
    }

    /// <summary>起后台轮询循环（幂等——重复调用不叠加）。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loopTask is not null)
            {
                return;
            }

            _loopCts = new CancellationTokenSource();
            CancellationToken token = _loopCts.Token;
            _loopTask = Task.Run(() => LoopAsync(token));
        }
    }

    /// <summary>停后台循环（面板关闭时调用）。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_loopCts is not null)
            {
                _loopCts.Cancel();
                _loopCts.Dispose();
                _loopCts = null;
            }

            _loopTask = null;
        }
    }

    /// <summary>读当前配置（副本）。</summary>
    /// <returns>配置对象。</returns>
    public RhConfig CurrentConfig()
    {
        lock (_gate)
        {
            return CloneConfig(_config);
        }
    }

    /// <summary>状态全景——面板一次取全（账号表 + 各工作区视图 + 事件）。</summary>
    /// <returns>状态对象。</returns>
    public RhStatus Snapshot()
    {
        var status = new RhStatus();
        status.OutDir = _outDir;

        RhConfig config;
        RhLedger ledger;
        lock (_gate)
        {
            config = CloneConfig(_config);
            ledger = _ledger;
            status.Events = new List<string>(_events);
        }

        status.Config = config;
        status.LastPoll = ledger.LastPoll;
        status.Accounts = BuildAccountRows(ledger);
        status.Workspaces = BuildWorkspaceViews(config, ledger);
        return status;
    }
    /// <summary>额度概览——合计推测剩余 + 最早刷新时刻（外部健康检查与面板共用）。</summary>
    /// <returns>概览对象。</returns>
    public RhSummary Summary()
    {
        AccountSnapshot snapshot = _collect.Snapshot;
        RhLedger ledger;
        lock (_gate)
        {
            ledger = _ledger;
        }

        var summary = new RhSummary();
        summary.Accounts = snapshot.Accounts.Count;
        summary.CollectedAt = snapshot.GeneratedAt;

        DateTime earliest = DateTime.MinValue;
        foreach (AccountSlot slot in snapshot.Accounts)
        {
            double? remain = RemainOf(slot, ledger);
            if (remain.HasValue)
            {
                summary.Known = summary.Known + 1;
                summary.Remain = summary.Remain + remain.Value;
            }

            DateTime refresh;
            if (DateTime.TryParseExact(slot.CoinRefreshTime, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out refresh))
            {
                if (earliest == DateTime.MinValue || refresh < earliest)
                {
                    earliest = refresh;
                }
            }
        }

        if (earliest != DateTime.MinValue)
        {
            summary.ResetAt = earliest.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            summary.ResetInMinutes = (int)Math.Round((earliest - DateTime.Now).TotalMinutes);
        }

        return summary;
    }

    /// <summary>提交一条音频——挑账号 → 上传 → 起任务 → 记账（同音频在同一工作区已有未完成任务即复用）。</summary>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="audioPath">音频完整路径。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>提交结果。</returns>
    public async Task<RhSubmitResult> SubmitAsync(string workspaceId, string audioPath, CancellationToken ct)
    {
        await _workGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = new RhSubmitResult();
            if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            {
                result.Message = "音频不在：" + audioPath;
                return result;
            }

            RhWorkspace? workspace = FindWorkspace(workspaceId);
            if (workspace is null)
            {
                result.Message = "没有这个工作区：" + workspaceId;
                return result;
            }

            if (workspace.WorkflowId.Length == 0)
            {
                result.Message = "工作区「" + workspace.Name + "」还没填工作流 id——先在工作区头部填上";
                return result;
            }

            string ident = IdentOf(workspace.Id, audioPath);
            lock (_gate)
            {
                RhTaskEntry? existing;
                if (_ledger.Tasks.TryGetValue(ident, out existing))
                {
                    if (existing.Status != "FAILED" && existing.Status != "CANCELED")
                    {
                        result.Ok = true;
                        result.Reused = true;
                        result.Id = ident;
                        result.TaskId = existing.TaskId;
                        result.Account = existing.Account;
                        result.Message = "该音频已在本工作区的任务里（" + existing.Status + "），不重复提交";
                        return result;
                    }
                }
            }

            // [段1] 挑账号——顺序取第一个「已登录 · 密钥在位 · 推测剩余 ≥ 20」
            string reason;
            AccountSlot? slot = PickAccount(out reason);
            if (slot is null)
            {
                result.Message = reason;
                Log("提交失败：" + reason);
                return result;
            }

            // [段2] 上传 + 起任务（工作流 id 由工作区携带）
            string uploaded = await _client.UploadAsync(audioPath, slot.ApiKey, UploadTimeoutSeconds, ct).ConfigureAwait(false);
            string taskId = await _client.RunAsync(workspace.WorkflowId, slot.ApiKey, uploaded, ApiTimeoutSeconds, ct).ConfigureAwait(false);

            // [段3] 记账（本地消耗等任务完成拿到 consumeCoins 再累加）
            var entry = new RhTaskEntry();
            entry.Id = ident;
            entry.WorkspaceId = workspace.Id;
            entry.Kind = RhWorkspace.KindAsr;
            entry.File = audioPath;
            entry.Size = new FileInfo(audioPath).Length;
            entry.TaskId = taskId;
            entry.Account = slot.ShortcutName;
            entry.Status = "QUEUED";
            entry.SubmittedAt = Clock.Now();

            lock (_gate)
            {
                _ledger.Tasks[ident] = entry;
                _ledgerStore.Save(_ledger);
            }

            result.Ok = true;
            result.Id = ident;
            result.TaskId = taskId;
            result.Account = slot.ShortcutName;
            result.Message = "已提交（" + workspace.Name + " · 账号 " + slot.ShortcutName + " · 任务 " + taskId + "）";
            Log("提交 " + Path.GetFileName(audioPath) + " → 任务 " + taskId + "（" + workspace.Name + " · 账号 " + slot.ShortcutName + "）");
            return result;
        }
        catch (Exception ex)
        {
            Log("提交异常：" + ex.Message);
            return new RhSubmitResult { Ok = false, Message = ex.Message };
        }
        finally
        {
            _workGate.Release();
        }
    }

    /// <summary>提交一次生成——挑账号 → 传图 → 起工作流（图 + 文两个节点）→ 记账。</summary>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="imagePath">图片完整路径。</param>
    /// <param name="prompt">提示词。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>提交结果。</returns>
    public async Task<RhSubmitResult> SubmitGenAsync(string workspaceId, string imagePath, string prompt, CancellationToken ct)
    {
        await _workGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = new RhSubmitResult();
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                result.Message = "图片不在：" + imagePath;
                return result;
            }

            if (string.IsNullOrWhiteSpace(prompt))
            {
                result.Message = "提示词是空的——生成要一段文字";
                return result;
            }

            RhWorkspace? workspace = FindWorkspace(workspaceId);
            if (workspace is null)
            {
                result.Message = "没有这个工作区：" + workspaceId;
                return result;
            }

            if (workspace.Kind != RhWorkspace.KindGen)
            {
                result.Message = "工作区「" + workspace.Name + "」不是生成块（类型 " + workspace.Kind + "）";
                return result;
            }

            if (workspace.WorkflowId.Length == 0)
            {
                result.Message = "工作区「" + workspace.Name + "」还没填工作流 id——先在工作区头部填上";
                return result;
            }

            // [段1] 挑账号——顺序取第一个「已登录 · 密钥在位 · 推测剩余 ≥ 20」
            string reason;
            AccountSlot? slot = PickAccount(out reason);
            if (slot is null)
            {
                result.Message = reason;
                Log("提交失败：" + reason);
                return result;
            }

            // [段2] 传图 + 起任务（两个节点：图像 + 提示词）
            string uploaded = await _client.UploadAsync(imagePath, slot.ApiKey, UploadTimeoutSeconds, ct).ConfigureAwait(false);
            var nodes = new List<RhNodeField>();
            var imageNode = new RhNodeField();
            imageNode.NodeId = RhClient.GenImageNode;
            imageNode.FieldName = RhClient.GenImageField;
            imageNode.FieldValue = uploaded;
            nodes.Add(imageNode);
            var promptNode = new RhNodeField();
            promptNode.NodeId = RhClient.GenPromptNode;
            promptNode.FieldName = RhClient.GenPromptField;
            promptNode.FieldValue = prompt;
            nodes.Add(promptNode);

            string taskId = await _client.RunNodesAsync(workspace.WorkflowId, slot.ApiKey, nodes, ApiTimeoutSeconds, ct).ConfigureAwait(false);

            // [段3] 记账（本地消耗等任务完成拿到 consumeCoins 再累加）
            string ident = IdentOf(workspace.Id, imagePath);
            var entry = new RhTaskEntry();
            entry.Id = ident;
            entry.WorkspaceId = workspace.Id;
            entry.Kind = RhWorkspace.KindGen;
            entry.File = imagePath;
            entry.Prompt = prompt;
            entry.Size = new FileInfo(imagePath).Length;
            entry.TaskId = taskId;
            entry.Account = slot.ShortcutName;
            entry.Status = "QUEUED";
            entry.SubmittedAt = Clock.Now();

            lock (_gate)
            {
                _ledger.Tasks[ident] = entry;
                _ledgerStore.Save(_ledger);
            }

            result.Ok = true;
            result.Id = ident;
            result.TaskId = taskId;
            result.Account = slot.ShortcutName;
            result.Message = "已提交（" + workspace.Name + " · 账号 " + slot.ShortcutName + " · 任务 " + taskId + "）";
            Log("提交生成 " + Path.GetFileName(imagePath) + " → 任务 " + taskId + "（" + workspace.Name + " · 账号 " + slot.ShortcutName + "）");
            return result;
        }
        catch (Exception ex)
        {
            Log("提交异常：" + ex.Message);
            return new RhSubmitResult { Ok = false, Message = ex.Message };
        }
        finally
        {
            _workGate.Release();
        }
    }

    /// <summary>轮询一轮未完成任务——完成的落产物 + 记消耗；失败的具名记因。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>本轮的完成 / 失败 / 进行中条数。</returns>
    public async Task<RhPollSummary> PollOnceAsync(CancellationToken ct)
    {
        await _workGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var summary = new RhPollSummary();
            List<RhTaskEntry> pending = new List<RhTaskEntry>();
            lock (_gate)
            {
                foreach (RhTaskEntry entry in _ledger.Tasks.Values)
                {
                    if (entry.Status != "SUCCESS" && entry.Status != "FAILED" && entry.Status != "CANCELED")
                    {
                        pending.Add(entry);
                    }
                }
            }

            foreach (RhTaskEntry entry in pending)
            {
                await PollOneAsync(entry, summary, ct).ConfigureAwait(false);
            }

            lock (_gate)
            {
                _ledger.LastPoll = Clock.Now();
                _ledgerStore.Save(_ledger);
            }

            return summary;
        }
        finally
        {
            _workGate.Release();
        }
    }

    /// <summary>取消一个未完成任务（取消不产生 RH 币消耗）。</summary>
    /// <param name="ident">标识（音频完整路径）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>结果说明。</returns>
    public async Task<string> CancelAsync(string ident, CancellationToken ct)
    {
        await _workGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RhTaskEntry? entry;
            lock (_gate)
            {
                if (!_ledger.Tasks.TryGetValue(ident, out entry))
                {
                    return "账本里没有这条任务";
                }
            }

            if (entry.Status == "SUCCESS" || entry.Status == "FAILED" || entry.Status == "CANCELED")
            {
                return "任务已结束（" + entry.Status + "），无需取消";
            }

            string apiKey = ApiKeyOf(entry.Account);
            if (apiKey.Length == 0)
            {
                return "找不到账号「" + entry.Account + "」的密钥——先在面板采集一次";
            }

            await _client.CancelAsync(apiKey, entry.TaskId, ApiTimeoutSeconds, ct).ConfigureAwait(false);
            lock (_gate)
            {
                entry.Status = "CANCELED";
                entry.FinishedAt = Clock.Now();
                _ledgerStore.Save(_ledger);
            }

            Log("已取消 " + Path.GetFileName(entry.File) + "（任务 " + entry.TaskId + "）");
            return "已取消";
        }
        catch (Exception ex)
        {
            Log("取消失败：" + ex.Message);
            return "取消失败：" + ex.Message;
        }
        finally
        {
            _workGate.Release();
        }
    }

    /// <summary>目录扫描——把工作区配置目录里的音频列出来（未提交的排前）。</summary>
    /// <param name="workspace">工作区。</param>
    /// <returns>音频完整路径清单。</returns>
    private List<string> ScanAudioFiles(RhWorkspace workspace)
    {
        var files = new List<string>();
        var pending = new List<string>();
        var done = new List<string>();

        if (workspace.AudioFile.Length > 0 && File.Exists(workspace.AudioFile))
        {
            pending.Add(workspace.AudioFile);
        }

        if (workspace.AudioDir.Length > 0 && Directory.Exists(workspace.AudioDir))
        {
            string[] found = Directory.GetFiles(workspace.AudioDir);
            foreach (string path in found)
            {
                if (!IsAudio(path))
                {
                    continue;
                }

                bool submitted;
                lock (_gate)
                {
                    submitted = _ledger.Tasks.ContainsKey(workspace.Id + "|" + path);
                }

                if (submitted)
                {
                    done.Add(path);
                }
                else
                {
                    pending.Add(path);
                }
            }
        }

        pending.Sort(StringComparer.OrdinalIgnoreCase);
        done.Sort(StringComparer.OrdinalIgnoreCase);
        files.AddRange(pending);
        files.AddRange(done);
        return files;
    }

    /// <summary>任务标识——工作区 id + 音频路径（同一音频在不同工作区各算一条）。</summary>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="audioPath">音频完整路径。</param>
    /// <returns>标识串。</returns>
    private static string IdentOf(string workspaceId, string audioPath)
    {
        return workspaceId + "|" + audioPath;
    }

    /// <summary>是不是认作音频的文件。</summary>
    /// <param name="path">文件路径。</param>
    /// <returns>是则 true。</returns>
    private static bool IsAudio(string path)
    {
        string suffix = Path.GetExtension(path).ToLowerInvariant();
        foreach (string candidate in AudioExtensions)
        {
            if (string.Equals(candidate, suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
    /// <summary>工作区视图清单——每块一块（音频清单 + 该块的任务）。</summary>
    /// <param name="config">当前配置。</param>
    /// <param name="ledger">当前账本。</param>
    /// <returns>工作区视图清单。</returns>
    private List<RhWorkspaceView> BuildWorkspaceViews(RhConfig config, RhLedger ledger)
    {
        var views = new List<RhWorkspaceView>();
        foreach (RhWorkspace workspace in config.Workspaces)
        {
            var view = new RhWorkspaceView();
            view.Id = workspace.Id;
            view.Name = workspace.Name;
            view.WorkflowId = workspace.WorkflowId;
            view.BuiltIn = workspace.BuiltIn;
            view.Kind = workspace.Kind;
            view.ImageFile = workspace.ImageFile;
            view.Prompt = workspace.Prompt;
            view.AudioFile = workspace.AudioFile;
            view.AudioDir = workspace.AudioDir;
            view.AutoPush = workspace.AutoPush;
            view.AudioFiles = ScanAudioFiles(workspace);
            view.Tasks = BuildTaskViews(ledger, workspace.Id);
            views.Add(view);
        }

        return views;
    }

    /// <summary>按 id 找工作区——返回副本（改配置走专用方法，不直接改副本）。</summary>
    /// <param name="workspaceId">工作区 id。</param>
    /// <returns>工作区副本；找不到为 null。</returns>
    private RhWorkspace? FindWorkspace(string workspaceId)
    {
        lock (_gate)
        {
            foreach (RhWorkspace workspace in _config.Workspaces)
            {
                if (string.Equals(workspace.Id, workspaceId, StringComparison.OrdinalIgnoreCase))
                {
                    return CloneWorkspace(workspace);
                }
            }
        }

        return null;
    }
    /// <summary>按 id 找工作区（对外只读副本——端点层用）。</summary>
    /// <param name="workspaceId">工作区 id。</param>
    /// <returns>工作区副本；找不到为 null。</returns>
    public RhWorkspace? FindWorkspaceOrNull(string workspaceId)
    {
        return FindWorkspace(workspaceId);
    }

    /// <summary>工作区副本。</summary>
    /// <param name="workspace">源工作区。</param>
    /// <returns>副本。</returns>
    private static RhWorkspace CloneWorkspace(RhWorkspace workspace)
    {
        var copy = new RhWorkspace();
        copy.Id = workspace.Id;
        copy.Name = workspace.Name;
        copy.WorkflowId = workspace.WorkflowId;
        copy.Kind = workspace.Kind;
        copy.ImageFile = workspace.ImageFile;
        copy.Prompt = workspace.Prompt;
        copy.AudioFile = workspace.AudioFile;
        copy.AudioDir = workspace.AudioDir;
        copy.AutoPush = workspace.AutoPush;
        copy.BuiltIn = workspace.BuiltIn;
        return copy;
    }

    /// <summary>改某生成工作区的输入面——图片与提示词（null = 不改）。</summary>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="imageFile">选中的图片完整路径（null = 不改；空串 = 清掉）。</param>
    /// <param name="prompt">提示词（null = 不改）。</param>
    /// <returns>落盘后的工作区副本；找不到或不是生成块为 null。</returns>
    public RhWorkspace? UpdateWorkspaceGen(string workspaceId, string? imageFile, string? prompt)
    {
        lock (_gate)
        {
            RhWorkspace? target = FindWorkspaceLocked(workspaceId);
            if (target is null || target.Kind != RhWorkspace.KindGen)
            {
                return null;
            }

            if (imageFile is not null)
            {
                target.ImageFile = imageFile;
            }

            if (prompt is not null)
            {
                target.Prompt = prompt;
            }

            _configStore.Save(_config);
            return CloneWorkspace(target);
        }
    }

    /// <summary>改某工作区的音频来源——文件与目录二选一（另一个传空串即清掉）。</summary>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="audioFile">选中的音频文件（null = 不改；空串 = 清掉）。</param>
    /// <param name="audioDir">选中的音频目录（null = 不改；空串 = 清掉）。</param>
    /// <returns>落盘后的工作区副本；找不到为 null。</returns>
    public RhWorkspace? UpdateWorkspaceAudio(string workspaceId, string? audioFile, string? audioDir)
    {
        lock (_gate)
        {
            RhWorkspace? target = null;
            foreach (RhWorkspace workspace in _config.Workspaces)
            {
                if (string.Equals(workspace.Id, workspaceId, StringComparison.OrdinalIgnoreCase))
                {
                    target = workspace;
                    break;
                }
            }

            if (target is null)
            {
                return null;
            }

            if (audioFile is not null)
            {
                target.AudioFile = audioFile;
                if (audioFile.Length > 0)
                {
                    target.AudioDir = "";
                }
            }

            if (audioDir is not null)
            {
                target.AudioDir = audioDir;
                if (audioDir.Length > 0)
                {
                    target.AudioFile = "";
                }
            }

            _configStore.Save(_config);
            return CloneWorkspace(target);
        }
    }

    /// <summary>改某工作区的自动推开关。</summary>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="enabled">是否开启。</param>
    /// <returns>落盘后的工作区副本；找不到为 null。</returns>
    public RhWorkspace? SetWorkspaceAutoPush(string workspaceId, bool enabled)
    {
        lock (_gate)
        {
            RhWorkspace? target = FindWorkspaceLocked(workspaceId);
            if (target is null)
            {
                return null;
            }

            target.AutoPush = enabled;
            _configStore.Save(_config);
            return CloneWorkspace(target);
        }
    }

    /// <summary>改某工作区的显示名与云端工作流 id（预留块填 id 用；内置块只改 id 不改身份）。</summary>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="name">新显示名（null / 空 = 不改）。</param>
    /// <param name="workflowId">新工作流 id（null / 空 = 不改）。</param>
    /// <returns>落盘后的工作区副本；找不到为 null。</returns>
    public RhWorkspace? SetWorkspaceWorkflow(string workspaceId, string? name, string? workflowId)
    {
        lock (_gate)
        {
            RhWorkspace? target = FindWorkspaceLocked(workspaceId);
            if (target is null)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(name))
            {
                target.Name = name;
            }

            if (!string.IsNullOrEmpty(workflowId))
            {
                target.WorkflowId = workflowId;
            }

            _configStore.Save(_config);
            Log("工作区「" + target.Name + "」工作流 id = " + target.WorkflowId);
            return CloneWorkspace(target);
        }
    }

    /// <summary>新增一个工作区（预留其他线上识别用——后续加新工作流走这里）。</summary>
    /// <param name="name">显示名。</param>
    /// <param name="workflowId">云端工作流 id（可空——先占位后填）。</param>
    /// <returns>新工作区副本；重名或空名为 null。</returns>
    public RhWorkspace? AddWorkspace(string name, string workflowId)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        lock (_gate)
        {
            foreach (RhWorkspace workspace in _config.Workspaces)
            {
                if (string.Equals(workspace.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            var created = new RhWorkspace();
            created.Id = NewWorkspaceId(name);
            created.Name = name;
            created.WorkflowId = workflowId ?? "";
            _config.Workspaces.Add(created);
            _configStore.Save(_config);
            Log("新增工作区「" + name + "」（id " + created.Id + " · 工作流 " + (created.WorkflowId.Length > 0 ? created.WorkflowId : "待填") + "）");
            return CloneWorkspace(created);
        }
    }

    /// <summary>删除一个工作区——内置块不给删；连带清掉它名下的任务记录。</summary>
    /// <param name="workspaceId">工作区 id。</param>
    /// <returns>结果说明。</returns>
    public string RemoveWorkspace(string workspaceId)
    {
        lock (_gate)
        {
            RhWorkspace? target = FindWorkspaceLocked(workspaceId);
            if (target is null)
            {
                return "没有这个工作区：" + workspaceId;
            }

            if (target.BuiltIn)
            {
                return "内置工作区「" + target.Name + "」不给删";
            }

            var kept = new List<RhWorkspace>();
            foreach (RhWorkspace workspace in _config.Workspaces)
            {
                if (!string.Equals(workspace.Id, workspaceId, StringComparison.OrdinalIgnoreCase))
                {
                    kept.Add(workspace);
                }
            }

            _config.Workspaces = kept;

            var orphans = new List<string>();
            foreach (string ident in _ledger.Tasks.Keys)
            {
                RhTaskEntry entry = _ledger.Tasks[ident];
                if (string.Equals(entry.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
                {
                    orphans.Add(ident);
                }
            }

            foreach (string ident in orphans)
            {
                _ledger.Tasks.Remove(ident);
            }

            if (string.Equals(_config.ActiveWorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
            {
                _config.ActiveWorkspaceId = "";
            }

            _configStore.Save(_config);
            _ledgerStore.Save(_ledger);
            Log("删除工作区「" + target.Name + "」（连带清掉 " + orphans.Count + " 条任务）");
            return "已删除工作区「" + target.Name + "」（连带清掉 " + orphans.Count + " 条任务）";
        }
    }

    /// <summary>锁内按 id 找工作区（调用方须持锁）。</summary>
    /// <param name="workspaceId">工作区 id。</param>
    /// <returns>工作区实例；找不到为 null。</returns>
    private RhWorkspace? FindWorkspaceLocked(string workspaceId)
    {
        foreach (RhWorkspace workspace in _config.Workspaces)
        {
            if (string.Equals(workspace.Id, workspaceId, StringComparison.OrdinalIgnoreCase))
            {
                return workspace;
            }
        }

        return null;
    }

    /// <summary>生成工作区 id——名字里的 ASCII 字母数字，撞了加序号。</summary>
    /// <param name="name">显示名。</param>
    /// <returns>唯一 id。</returns>
    private string NewWorkspaceId(string name)
    {
        var builder = new StringBuilder();
        foreach (char c in name)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        string stem = builder.ToString();
        if (stem.Length == 0)
        {
            stem = "ws";
        }

        string candidate = stem;
        int index = 2;
        while (FindWorkspaceLocked(candidate) is not null)
        {
            candidate = stem + index.ToString(CultureInfo.InvariantCulture);
            index = index + 1;
        }

        return candidate;
    }

    /// <summary>账号表——含推测剩余与可用性（**不含密钥**）。</summary>
    /// <param name="ledger">当前账本。</param>
    /// <returns>账号行清单。</returns>
    private List<RhAccountRow> BuildAccountRows(RhLedger ledger)
    {
        var rows = new List<RhAccountRow>();
        AccountSnapshot snapshot = _collect.Snapshot;
        foreach (AccountSlot slot in snapshot.Accounts)
        {
            var row = new RhAccountRow();
            row.Account = slot.ShortcutName;
            row.NickName = slot.NickName;
            row.Status = slot.Status;

            double? remain = RemainOf(slot, ledger);
            row.Remain = remain;

            if (slot.Status != "ok")
            {
                row.Note = "未登录 / 采集异常——先在主表处理";
            }
            else if (slot.ApiKey.Length == 0)
            {
                row.Note = "密钥不在（等一次采集）";
            }
            else if (!remain.HasValue)
            {
                row.Note = "未取到算力明细——不判可用";
            }
            else if (remain.Value <= 0)
            {
                row.Note = "推测剩余已用尽";
            }
            else if (remain.Value < MinRemainToPick)
            {
                row.Note = "推测剩余不足 " + MinRemainToPick + "——不取用";
            }
            else
            {
                row.Usable = true;
                row.Note = "可用";
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>推测剩余——算力额度 − 自获取以来花费 − 本次采集之后的本地消耗（下限 0）。</summary>
    /// <param name="slot">账号槽位。</param>
    /// <param name="ledger">当前账本。</param>
    /// <returns>剩余（RH 币）；取不到算力明细时为 null。</returns>
    private static double? RemainOf(AccountSlot slot, RhLedger ledger)
    {
        if (!slot.CoinGainAmount.HasValue)
        {
            return null;
        }

        double amount = slot.CoinGainAmount.Value;
        double spent = 0;
        if (slot.CoinSpent.HasValue)
        {
            spent = slot.CoinSpent.Value;
        }

        double local = 0;
        RhSpend? record;
        if (ledger.Spends.TryGetValue(slot.ShortcutPath, out record))
        {
            if (string.Equals(record.SinceCollectedAt, slot.CollectedAt, StringComparison.Ordinal))
            {
                local = record.Spent;
            }
        }

        double remain = amount - spent - local;
        if (remain < 0)
        {
            remain = 0;
        }

        return remain;
    }

    /// <summary>挑账号——顺序取第一个可用的；全不可用时给出具名原因。</summary>
    /// <param name="reason">不可用原因。</param>
    /// <returns>账号槽位；无可用时返回 null。</returns>
    private AccountSlot? PickAccount(out string reason)
    {
        AccountSnapshot snapshot = _collect.Snapshot;
        var blocked = new List<string>();
        RhLedger ledger;
        lock (_gate)
        {
            ledger = _ledger;
        }

        foreach (AccountSlot slot in snapshot.Accounts)
        {
            if (slot.Status != "ok")
            {
                continue;
            }

            if (slot.ApiKey.Length == 0)
            {
                blocked.Add(slot.ShortcutName + "（密钥不在）");
                continue;
            }

            double? remain = RemainOf(slot, ledger);
            if (!remain.HasValue)
            {
                blocked.Add(slot.ShortcutName + "（未取到算力明细）");
                continue;
            }

            if (remain.Value >= MinRemainToPick)
            {
                reason = "";
                return slot;
            }

            blocked.Add(slot.ShortcutName + "（推测剩余 " + remain.Value + " 低于 " + MinRemainToPick + "）");
        }

        if (blocked.Count == 0)
        {
            reason = "没有可用账号——先「全量采集」，或检查账号登录态";
        }
        else
        {
            reason = "没有可用账号：" + string.Join(" · ", blocked);
        }

        return null;
    }

    /// <summary>按账号名（快捷方式名）取密钥——找不到返回空串（不回落成别的账号）。</summary>
    /// <param name="account">快捷方式名。</param>
    /// <returns>密钥；找不到为空串。</returns>
    private string ApiKeyOf(string account)
    {
        AccountSnapshot snapshot = _collect.Snapshot;
        foreach (AccountSlot slot in snapshot.Accounts)
        {
            if (string.Equals(slot.ShortcutName, account, StringComparison.OrdinalIgnoreCase))
            {
                return slot.ApiKey;
            }
        }

        return "";
    }

    /// <summary>轮询一条任务——完成则落产物并记消耗。</summary>
    /// <param name="entry">任务记录。</param>
    /// <param name="summary">本轮汇总。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    private async Task PollOneAsync(RhTaskEntry entry, RhPollSummary summary, CancellationToken ct)
    {
        string apiKey = ApiKeyOf(entry.Account);
        if (apiKey.Length == 0)
        {
            lock (_gate)
            {
                entry.Status = "FAILED";
                entry.Error = "找不到账号「" + entry.Account + "」的密钥";
                entry.FinishedAt = Clock.Now();
                _ledgerStore.Save(_ledger);
            }

            summary.Failed = summary.Failed + 1;
            Log("任务失败 " + Path.GetFileName(entry.File) + "：" + entry.Error);
            return;
        }

        RhQueryResult result;
        try
        {
            result = await _client.QueryAsync(apiKey, entry.TaskId, ApiTimeoutSeconds, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 查询失败不终结任务——下一轮再查（网络抖动常见）
            Log("查询失败 " + Path.GetFileName(entry.File) + "：" + ex.Message);
            return;
        }

        lock (_gate)
        {
            entry.CheckedAt = Clock.Now();
        }

        if (result.ErrorCode.Length > 0)
        {
            lock (_gate)
            {
                entry.Status = "FAILED";
                entry.Error = "任务不存在或已过期（" + result.ErrorCode + " " + result.ErrorMessage + "）";
                entry.FinishedAt = Clock.Now();
                _ledgerStore.Save(_ledger);
            }

            summary.Failed = summary.Failed + 1;
            Log("任务失败 " + Path.GetFileName(entry.File) + "：" + entry.Error);
            return;
        }

        if (result.Status == "SUCCESS")
        {
            // [段1] 落产物——按任务类型分流（asr 认 text_ / time_ 前缀的 txt；gen 认 latent 并内置出图）
            try
            {
                List<RhArtifact> artifacts;
                if (entry.Kind == RhWorkspace.KindGen)
                {
                    artifacts = await SaveGenArtifactsAsync(result, entry, ct).ConfigureAwait(false);
                }
                else
                {
                    artifacts = await SaveArtifactsAsync(result, ct).ConfigureAwait(false);
                }
                lock (_gate)
                {
                    entry.Artifacts = artifacts;
                    entry.Coins = result.ConsumeCoins;
                    entry.Status = "SUCCESS";
                    entry.FinishedAt = Clock.Now();
                    AddLocalSpend(entry.Account, result.ConsumeCoins);
                    _ledgerStore.Save(_ledger);
                }

                // [段2] 回传即刷新该账号快照——余额 / 花费与站点对齐（同账号已在队列里则合并）
                QueueAccountRefresh(entry.Account);

                summary.Finished = summary.Finished + 1;
                Log("完成 " + Path.GetFileName(entry.File) + "（" + result.ConsumeCoins + " 币 · " + artifacts.Count + " 个产物）");
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    entry.Error = "产物落位失败：" + ex.Message;
                    _ledgerStore.Save(_ledger);
                }

                summary.Failed = summary.Failed + 1;
                Log("产物落位失败 " + Path.GetFileName(entry.File) + "：" + ex.Message);
            }

            return;
        }

        if (result.Status == "FAILED")
        {
            lock (_gate)
            {
                entry.Status = "FAILED";
                entry.Error = result.ErrorCode + " " + result.ErrorMessage;
                entry.FinishedAt = Clock.Now();
                _ledgerStore.Save(_ledger);
            }

            summary.Failed = summary.Failed + 1;
            Log("任务失败 " + Path.GetFileName(entry.File) + "：" + entry.Error);
            return;
        }

        lock (_gate)
        {
            entry.Status = result.Status;
            _ledgerStore.Save(_ledger);
        }

        summary.Running = summary.Running + 1;
    }

    /// <summary>把回传产物落到 out 目录——只认 text_ / time_ 前缀的 txt。</summary>
    /// <param name="result">查询结果。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>落地产物清单。</returns>
    private async Task<List<RhArtifact>> SaveArtifactsAsync(RhQueryResult result, CancellationToken ct)
    {
        var artifacts = new List<RhArtifact>();
        foreach (RhResultFile file in result.Results)
        {
            string name = FileNameOf(file.Url);
            if (name.Length == 0)
            {
                continue;
            }

            if (!name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!name.StartsWith("text_", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("time_", StringComparison.OrdinalIgnoreCase))
            {
                Log("跳过非约定产物：" + name);
                continue;
            }

            string dest = Path.Combine(_outDir, name);
            long bytes = await _client.DownloadAsync(file.Url, dest, DownloadTimeoutSeconds, ct).ConfigureAwait(false);
            var artifact = new RhArtifact();
            artifact.Name = name;
            artifact.Path = dest;
            artifact.Bytes = bytes;
            artifacts.Add(artifact);
            Log("落位 " + name + "（" + bytes + " 字节）");
        }

        return artifacts;
    }
    /// <summary>把随机种子文本收成安全的文件名主干——去掉非法字符，限长 64。</summary>
    /// <param name="raw">种子原文。</param>
    /// <returns>可作文件名的字符串（全被滤掉时回落 seed）。</returns>
    private static string SafeFileStem(string raw)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        System.Text.StringBuilder builder = new System.Text.StringBuilder();
        for (int i = 0; i < raw.Length; i = i + 1)
        {
            char c = raw[i];
            bool bad = false;
            for (int k = 0; k < invalid.Length; k = k + 1)
            {
                if (c == invalid[k])
                {
                    bad = true;
                    break;
                }
            }

            if (!bad)
            {
                builder.Append(c);
            }

            if (builder.Length >= 64)
            {
                break;
            }
        }

        string cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? "seed" : cleaned;
    }

    /// <summary>把生成任务的产物落到该工作区子目录——文本产物：base64 无损 PNG 还原成 png，其余（随机种子那类）原样留档；latent 产物换装成自建中间态 `.cdlat`，再由中间态出 png。</summary>
    /// <param name="result">查询结果。</param>
    /// <param name="entry">任务条目（取工作区 id）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>落地产物清单（还原 / 解出的 png；latent 口径另带中间态一条）。</returns>
    private async Task<List<RhArtifact>> SaveGenArtifactsAsync(RhQueryResult result, RhTaskEntry entry, CancellationToken ct)
    {
        var artifacts = new List<RhArtifact>();
        string dir = Path.Combine(_outDir, entry.WorkspaceId);
        string seed = "";
        string imageName = "";
        byte[]? imageBytes = null;

        foreach (RhResultFile file in result.Results)
        {
            string name = FileNameOf(file.Url);
            if (name.Length == 0)
            {
                continue;
            }

            // [段1] 认两种产物——latent（旧口径）与 txt（线上把无损 PNG 的 base64 装进文本，走 SaveText 节点）
            bool isLatent = string.Equals(file.OutputType, "latent", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".latent", StringComparison.OrdinalIgnoreCase);
            bool isText = string.Equals(file.OutputType, "txt", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
            if (!isLatent && !isText)
            {
                Log("跳过不认识的产物：" + name);
                continue;
            }

            string dest = Path.Combine(dir, name);
            long bytes = await _client.DownloadAsync(file.Url, dest, DownloadTimeoutSeconds, ct).ConfigureAwait(false);
            Log("下载 " + name + "（" + bytes + " 字节）");

            // [段2] 文本产物——base64 无损 PNG 取内容、随机种子取数值；文本文件本身不留（落盘只留一个图像文件）
            if (isText)
            {
                string text = File.ReadAllText(dest).Trim();
                File.Delete(dest);

                if (text.StartsWith("iVBORw0KGgo", StringComparison.Ordinal))
                {
                    imageBytes = Convert.FromBase64String(text);
                    imageName = Path.GetFileNameWithoutExtension(name) + ".png";
                    Log("取到无损 PNG（文本 " + name + " 共 " + bytes + " 字节 → 图像 " + imageBytes.Length + " 字节）");
                }
                else
                {
                    seed = text;
                    int head = seed.Length < 120 ? seed.Length : 120;
                    Log("取到随机种子：" + seed.Substring(0, head));
                }

                continue;
            }

            // [段3] latent 产物——收成自建中间态 `.cdlat`（校验通过才落盘），再由中间态出 png
            string packPath = Path.ChangeExtension(dest, ".cdlat");
            LatentPackResult pack = LatentPack.Pack(dest, packPath);
            var packed = new RhArtifact();
            packed.Name = Path.GetFileName(packPath);
            packed.Path = packPath;
            packed.Bytes = pack.PackBytes;
            artifacts.Add(packed);

            // 原 latent 只作下载中转——中间态已逐位校验（`--cdlat-unpack` 可随时还原），故删掉不留大文件
            File.Delete(dest);
            string mode = pack.Mode == LatentPack.ModeGrid ? "8 位图像无损" : "F32 逐位无损回落";
            Log("中间态 " + packed.Name + "（" + pack.PackBytes + " 字节 · " + mode + " · 原 latent " + pack.SourceBytes + " 字节已让位）");

            string pngPath = Path.ChangeExtension(dest, ".png");
            LatentPixels pixels = LatentCodec.ToPixels(LatentPack.Unpack(packPath));
            LatentCodec.WritePng(pngPath, pixels);
            var image = new RhArtifact();
            image.Name = Path.GetFileName(pngPath);
            image.Path = pngPath;
            image.Bytes = new FileInfo(pngPath).Length;
            artifacts.Add(image);
            Log("出图 " + image.Name + "（" + pixels.Width + "×" + pixels.Height + " · " + image.Bytes + " 字节）");
        }

        // [段4] 落盘——有种子就按种子命名（同种子即同图，直接覆盖），没有就沿用线上原名
        if (imageBytes != null)
        {
            string finalName = seed.Length > 0 ? SafeFileStem(seed) + ".png" : imageName;
            string finalPath = Path.Combine(dir, finalName);
            File.WriteAllBytes(finalPath, imageBytes);

            var restored = new RhArtifact();
            restored.Name = finalName;
            restored.Path = finalPath;
            restored.Bytes = imageBytes.Length;
            artifacts.Add(restored);
            Log("出图 " + finalName + "（" + imageBytes.Length + " 字节 · 无损 PNG）");
        }

        return artifacts;
    }

    /// <summary>从下载地址里取文件名。</summary>
    /// <param name="url">下载地址。</param>
    /// <returns>文件名；取不到为空串。</returns>
    private static string FileNameOf(string url)
    {
        string trimmed = url;
        int query = trimmed.IndexOf('?');
        if (query >= 0)
        {
            trimmed = trimmed.Substring(0, query);
        }

        int slash = trimmed.LastIndexOf('/');
        if (slash >= 0)
        {
            trimmed = trimmed.Substring(slash + 1);
        }

        return trimmed.Trim();
    }

    /// <summary>累加某账号的本地消耗——采集刷新后自动归零重记。</summary>
    /// <param name="account">快捷方式名。</param>
    /// <param name="coins">本次消耗。</param>
    private void AddLocalSpend(string account, double coins)
    {
        AccountSnapshot snapshot = _collect.Snapshot;
        string collectedAt = "";
        string shortcutPath = "";
        foreach (AccountSlot slot in snapshot.Accounts)
        {
            if (string.Equals(slot.ShortcutName, account, StringComparison.OrdinalIgnoreCase))
            {
                collectedAt = slot.CollectedAt;
                shortcutPath = slot.ShortcutPath;
                break;
            }
        }

        if (shortcutPath.Length == 0)
        {
            return;
        }

        RhSpend? record;
        if (!_ledger.Spends.TryGetValue(shortcutPath, out record))
        {
            record = new RhSpend();
            _ledger.Spends[shortcutPath] = record;
        }

        if (!string.Equals(record.SinceCollectedAt, collectedAt, StringComparison.Ordinal))
        {
            record.SinceCollectedAt = collectedAt;
            record.Spent = 0;
        }

        record.Spent = record.Spent + coins;
    }
    /// <summary>任务回传后刷新该账号的快照——同一账号已有待执行的刷新即跳过（合并重复请求）。</summary>
    /// <param name="account">快捷方式名。</param>
    private void QueueAccountRefresh(string account)
    {
        AccountSnapshot snapshot = _collect.Snapshot;
        string shortcutPath = "";
        foreach (AccountSlot slot in snapshot.Accounts)
        {
            if (string.Equals(slot.ShortcutName, account, StringComparison.OrdinalIgnoreCase))
            {
                shortcutPath = slot.ShortcutPath;
                break;
            }
        }

        if (shortcutPath.Length == 0)
        {
            return;
        }

        TaskQueueSnapshot queue = _collect.Tasks.Snapshot();
        if (queue.Running is not null
            && string.Equals(queue.Running.Kind, "collect-one", StringComparison.Ordinal)
            && string.Equals(queue.Running.TargetKey, shortcutPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (TaskRecord item in queue.Waiting)
        {
            if (string.Equals(item.Kind, "collect-one", StringComparison.Ordinal)
                && string.Equals(item.TargetKey, shortcutPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        string error;
        TaskRecord? queued = _collect.EnqueueOne(shortcutPath, out error);
        if (queued is null)
        {
            Log("账号快照刷新未入队：" + error);
        }
    }

    /// <summary>任务清单视图（最新在前）。</summary>
    /// <param name="ledger">当前账本。</param>
    /// <returns>任务视图清单。</returns>
    /// <param name="workspaceId">工作区 id（空 = 全部）。</param>
    private List<RhTaskView> BuildTaskViews(RhLedger ledger, string workspaceId)
    {
        var list = new List<RhTaskEntry>();
        foreach (RhTaskEntry entry in ledger.Tasks.Values)
        {
            list.Add(entry);
        }

        list.Sort(CompareBySubmittedDesc);

        var views = new List<RhTaskView>();
        int index = 0;
        foreach (RhTaskEntry entry in list)
        {
            if (index >= TaskViewLimit)
            {
                break;
            }

            var view = new RhTaskView();
            view.Id = entry.Id;
            view.Name = Path.GetFileName(entry.File);
            view.TaskId = entry.TaskId;
            view.Account = entry.Account;
            view.Status = entry.Status;
            view.Coins = entry.Coins;
            view.Error = entry.Error;
            view.SubmittedAt = entry.SubmittedAt;
            view.FinishedAt = entry.FinishedAt;
            foreach (RhArtifact artifact in entry.Artifacts)
            {
                view.Artifacts.Add(artifact.Name);
            }

            views.Add(view);
            index = index + 1;
        }

        return views;
    }

    /// <summary>按提交时刻倒序（字符串时刻可直接比较）。</summary>
    /// <param name="left">左项。</param>
    /// <param name="right">右项。</param>
    /// <returns>比较结果。</returns>
    private static int CompareBySubmittedDesc(RhTaskEntry left, RhTaskEntry right)
    {
        return string.CompareOrdinal(right.SubmittedAt, left.SubmittedAt);
    }

    /// <summary>后台循环——按间隔轮询未完成任务；自动推开着时顺带扫目录提交新文件。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollSeconds * 1000, ct).ConfigureAwait(false);
                await PollOnceAsync(ct).ConfigureAwait(false);
                await AutoPushAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log("后台轮询异常：" + ex.Message);
            }
        }
    }

    /// <summary>自动推——开关点亮且选了目录时，把目录里未提交的音频逐条提交。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    private async Task AutoPushAsync(CancellationToken ct)
    {
        RhConfig config;
        lock (_gate)
        {
            config = CloneConfig(_config);
        }

        foreach (RhWorkspace workspace in config.Workspaces)
        {
            // 只有音频转写块吃目录监听——生成块没有目录自动推
            if (workspace.Kind != RhWorkspace.KindAsr)
            {
                continue;
            }

            if (!workspace.AutoPush || workspace.AudioDir.Length == 0)
            {
                continue;
            }

            List<string> files = ScanAudioFiles(workspace);
            foreach (string path in files)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                bool submitted;
                lock (_gate)
                {
                    submitted = _ledger.Tasks.ContainsKey(IdentOf(workspace.Id, path));
                }

                if (submitted)
                {
                    continue;
                }

                RhSubmitResult result = await SubmitAsync(workspace.Id, path, ct).ConfigureAwait(false);
                if (!result.Ok)
                {
                    // 额度用尽这类系统性原因——出声停住，不逐条重试
                    Log("自动推停住（" + workspace.Name + "）：" + result.Message);
                    return;
                }
            }
        }
    }

    /// <summary>配置副本。</summary>
    /// <param name="config">源配置。</param>
    /// <returns>副本。</returns>
    private static RhConfig CloneConfig(RhConfig config)
    {
        var copy = new RhConfig();
        foreach (RhWorkspace workspace in config.Workspaces)
        {
            copy.Workspaces.Add(CloneWorkspace(workspace));
        }

        copy.ActiveWorkspaceId = config.ActiveWorkspaceId;
        return copy;
    }

    /// <summary>记一条事件（新在前，超出上限截断）。</summary>
    /// <param name="message">事件文本。</param>
    private void Log(string message)
    {
        string line = Clock.Now() + " · " + message;
        lock (_gate)
        {
            _events.Insert(0, line);
            if (_events.Count > EventLimit)
            {
                _events.RemoveRange(EventLimit, _events.Count - EventLimit);
            }
        }

        Console.WriteLine("[RH] " + message);
    }
}

/// <summary>一轮轮询的汇总。</summary>
public sealed class RhPollSummary
{
    /// <summary>本轮完成条数。</summary>
    public int Finished { get; set; }

    /// <summary>本轮失败条数。</summary>
    public int Failed { get; set; }

    /// <summary>仍在进行中的条数。</summary>
    public int Running { get; set; }
}
