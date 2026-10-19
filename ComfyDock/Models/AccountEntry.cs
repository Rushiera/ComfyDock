using System.Collections.Generic;

namespace ComfyDock.Models;

/// <summary>
/// 账号表文件形态——软件内置的账号源（唯一真相），落 <c>data\accounts-registry.json</c>。
/// 与套壳浏览器（ComfyDock.Shell）共读同写，字段名即契约。
/// </summary>
public sealed class AccountRegistry
{
    /// <summary>最后更新时间（yyyy-MM-dd HH:mm:ss）。</summary>
    public string UpdatedAt { get; set; } = "";

    /// <summary>账号列表。</summary>
    public List<AccountEntry> Accounts { get; set; } = new List<AccountEntry>();
}

/// <summary>
/// 一个账号——显示名 + 自己的浏览器数据目录（登录态一份，软件自持）。
/// </summary>
public sealed class AccountEntry
{
    /// <summary>账号 id（唯一，同时是数据目录名的默认来源）。</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名。</summary>
    public string Name { get; set; } = "";

    /// <summary>浏览器数据目录（相对数据根或绝对路径）。</summary>
    public string ProfileDir { get; set; } = "";

    /// <summary>一次性导入源（旧的 Chrome 形态用户目录）；空 = 不导入。</summary>
    public string ImportFrom { get; set; } = "";

    /// <summary>是否启用（停用的不出现在面板与采集清单）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>备注（手机号之类，人看）。</summary>
    public string Note { get; set; } = "";
}
