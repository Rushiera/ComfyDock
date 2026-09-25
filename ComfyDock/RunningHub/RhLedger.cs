using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ComfyDock.RunningHub;

/// <summary>一个落地产物（线上回传的 txt）。</summary>
public sealed class RhArtifact
{
    /// <summary>产物文件名（服务侧原名）。</summary>
    public string Name { get; set; } = "";

    /// <summary>落盘完整路径。</summary>
    public string Path { get; set; } = "";

    /// <summary>字节数。</summary>
    public long Bytes { get; set; }
}

/// <summary>一条线上识别任务（一个音频一条）。</summary>
public sealed class RhTaskEntry
{
    /// <summary>标识——音频完整路径 + 长度的稳定键。</summary>
    public string Id { get; set; } = "";

    /// <summary>所属工作区 id。</summary>
    public string WorkspaceId { get; set; } = "";

    /// <summary>工作区类型（asr / gen）——决定产物按哪套约定落。</summary>
    public string Kind { get; set; } = RhWorkspace.KindAsr;

    /// <summary>输入完整路径（音频 / 图片）。</summary>
    public string File { get; set; } = "";

    /// <summary>生成任务的提示词（asr 任务为空）。</summary>
    public string Prompt { get; set; } = "";

    /// <summary>音频字节数（标识的一部分）。</summary>
    public long Size { get; set; }

    /// <summary>RunningHub 任务号。</summary>
    public string TaskId { get; set; } = "";

    /// <summary>使用的账号（快捷方式名）。</summary>
    public string Account { get; set; } = "";

    /// <summary>状态：QUEUED / RUNNING / SUCCESS / FAILED / CANCELED。</summary>
    public string Status { get; set; } = "";

    /// <summary>本次消耗的 RH 币（服务端回传）。</summary>
    public double Coins { get; set; }

    /// <summary>失败原因（非空即出错）。</summary>
    public string Error { get; set; } = "";

    /// <summary>提交时刻。</summary>
    public string SubmittedAt { get; set; } = "";

    /// <summary>最近一次查询时刻。</summary>
    public string CheckedAt { get; set; } = "";

    /// <summary>完成时刻。</summary>
    public string FinishedAt { get; set; } = "";

    /// <summary>落地产物清单。</summary>
    public List<RhArtifact> Artifacts { get; set; } = new List<RhArtifact>();
}

/// <summary>
/// 某账号在「最近一次采集之后」的本地消耗——采集刷新即归零（避免与站点账单重复扣）。
/// </summary>
public sealed class RhSpend
{
    /// <summary>对应的采集时刻（与快照 slot.CollectedAt 比对）。</summary>
    public string SinceCollectedAt { get; set; } = "";

    /// <summary>此后本地新提交任务的消耗合计（RH 币）。</summary>
    public double Spent { get; set; }
}

/// <summary>线上识别账本——任务表 + 本地消耗，落盘 `data/rh-ledger.json`。</summary>
public sealed class RhLedger
{
    /// <summary>任务表（标识 → 任务）。</summary>
    public Dictionary<string, RhTaskEntry> Tasks { get; set; } = new Dictionary<string, RhTaskEntry>();

    /// <summary>本地消耗（快捷方式路径 → 消耗记录）。</summary>
    public Dictionary<string, RhSpend> Spends { get; set; } = new Dictionary<string, RhSpend>();

    /// <summary>最近一次轮询时刻。</summary>
    public string LastPoll { get; set; } = "";
}

/// <summary>
/// 账本读写——缺失即空账本；解析失败抛错（由调用方出声）。写入走原子替换。
/// </summary>
public sealed class RhLedgerStore
{
    /// <summary>JSON 序列化选项（缩进 + 驼峰 + 中文直出）。</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>账本文件完整路径。</summary>
    private readonly string _filePath;

    /// <summary>以账本文件路径构造。</summary>
    /// <param name="filePath">rh-ledger.json 完整路径。</param>
    public RhLedgerStore(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>账本文件完整路径。</summary>
    public string FilePath
    {
        get { return _filePath; }
    }

    /// <summary>读账本——文件缺失返回空账本；内容损坏抛错。</summary>
    /// <returns>账本对象。</returns>
    public RhLedger Load()
    {
        if (!File.Exists(_filePath))
        {
            return new RhLedger();
        }

        string json = File.ReadAllText(_filePath);
        RhLedger? ledger = JsonSerializer.Deserialize<RhLedger>(json, SerializerOptions);
        if (ledger is null)
        {
            throw new InvalidOperationException("账本形态不对：" + _filePath);
        }

        if (ledger.Tasks is null)
        {
            ledger.Tasks = new Dictionary<string, RhTaskEntry>();
        }

        if (ledger.Spends is null)
        {
            ledger.Spends = new Dictionary<string, RhSpend>();
        }

        return ledger;
    }

    /// <summary>写账本（原子替换——先落 .tmp 再改名）。</summary>
    /// <param name="ledger">待落盘账本。</param>
    public void Save(RhLedger ledger)
    {
        string? dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string temp = _filePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(ledger, SerializerOptions));
        File.Move(temp, _filePath, true);
    }
}
