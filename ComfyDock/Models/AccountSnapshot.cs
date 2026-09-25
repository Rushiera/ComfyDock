using System.Collections.Generic;

namespace ComfyDock.Models;

/// <summary>
/// 账号快照——一次全量采集的完整产物，落盘为 data/accounts.json。
/// </summary>
public sealed class AccountSnapshot
{
    /// <summary>本快照生成时刻（yyyy-MM-dd HH:mm:ss）。</summary>
    public string GeneratedAt { get; set; } = "";

    /// <summary>账号槽位列表。</summary>
    public List<AccountSlot> Accounts { get; set; } = new List<AccountSlot>();
}
