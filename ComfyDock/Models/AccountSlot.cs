using System;

namespace ComfyDock.Models;

/// <summary>
/// 账号槽位——面板一行：一个快捷方式 + 其指向的浏览器用户目录 + 采集结果。
/// </summary>
public sealed class AccountSlot
{
    /// <summary>快捷方式文件名（如 runninghub-1.lnk）。</summary>
    public string ShortcutName { get; set; } = "";

    /// <summary>快捷方式完整路径。</summary>
    public string ShortcutPath { get; set; } = "";

    /// <summary>浏览器用户数据目录（取自 lnk 的 --user-data-dir 参数）。</summary>
    public string ProfileDir { get; set; } = "";

    /// <summary>状态：ok / not-logged-in / error / running / unknown。</summary>
    public string Status { get; set; } = "unknown";

    /// <summary>平台用户 id。</summary>
    public string UserId { get; set; } = "";

    /// <summary>平台昵称。</summary>
    public string NickName { get; set; } = "";

    /// <summary>绑定手机号。</summary>
    public string Mobile { get; set; } = "";
    /// <summary>消费级 API 密钥（完整值——2026-09-25 一次性豁免，落盘 accounts.json；面板只展示前 4 位）。</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>RH 币余额（页面字段 totalCoin）；未采集为 null。</summary>
    public long? TotalCoin { get; set; }

    /// <summary>上次获取算力时间（买币历史页「算力获取明细」最新一条的获取时间）；未取到为空。</summary>
    public string CoinGainTime { get; set; } = "";

    /// <summary>上次获取的算力额度（RH 币）；未取到为 null。</summary>
    public long? CoinGainAmount { get; set; }

    /// <summary>该笔算力的刷新时间（固定为获取时间后 24 小时整）；未取到为空。</summary>
    public string CoinRefreshTime { get; set; } = "";

    /// <summary>自上次获取算力以来的 RH 币花费（任务账单明细求和）；未取到为 null。</summary>
    public long? CoinSpent { get; set; }

    /// <summary>本次采集时刻（yyyy-MM-dd HH:mm:ss）；未采集为空。</summary>
    public string CollectedAt { get; set; } = "";

    /// <summary>失败原因（status = error / running 时非空）。</summary>
    public string Error { get; set; } = "";

    /// <summary>该账号的工作流列表（workspace 采集）。</summary>
    public System.Collections.Generic.List<WorkflowItem> Workflows { get; set; } = new System.Collections.Generic.List<WorkflowItem>();

    /// <summary>工作流采集状态说明（空 = 正常）。</summary>
    public string WorkflowError { get; set; } = "";
}
