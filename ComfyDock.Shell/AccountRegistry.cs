using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ComfyDock.Shell;

/// <summary>
/// 账号表——软件内置的账号源（唯一真相），落 data\accounts-registry.json。
/// 一条 = 一个账号：显示名 + 该账号自己的浏览器数据目录（相对数据根或绝对路径）。
/// </summary>
public sealed class AccountRegistry
{
    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ShellOptions _options;
    private readonly List<ShellAccount> _accounts = new List<ShellAccount>();

    /// <summary>以数据根构造并立即读一次。</summary>
    /// <param name="options">套壳选项。</param>
    public AccountRegistry(ShellOptions options)
    {
        _options = options;
        Load();
    }

    /// <summary>当前账号表（只读视图）。</summary>
    public IReadOnlyList<ShellAccount> Accounts
    {
        get { return _accounts; }
    }

    /// <summary>账号表文件路径。</summary>
    public string FilePath
    {
        get { return _options.RegistryPath; }
    }

    /// <summary>重新读盘——文件缺失或损坏时按空表处理（不抛，避免套壳起不来）。</summary>
    public void Load()
    {
        _accounts.Clear();
        if (!File.Exists(_options.RegistryPath))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(_options.RegistryPath);
            AccountRegistryFile? file = JsonSerializer.Deserialize<AccountRegistryFile>(json, SerializerOptions);
            if (file == null || file.Accounts == null)
            {
                return;
            }

            foreach (ShellAccount account in file.Accounts)
            {
                if (string.IsNullOrWhiteSpace(account.Id))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(account.ProfileDir))
                {
                    account.ProfileDir = "auth/" + account.Id;
                }

                _accounts.Add(account);
            }
        }
        catch (JsonException)
        {
            _accounts.Clear();
        }
    }

    /// <summary>按 id 找账号。</summary>
    /// <param name="accountId">账号 id。</param>
    /// <returns>命中返回账号；未命中返回 null。</returns>
    public ShellAccount? Find(string accountId)
    {
        foreach (ShellAccount account in _accounts)
        {
            if (string.Equals(account.Id, accountId, StringComparison.Ordinal))
            {
                return account;
            }
        }

        return null;
    }

    /// <summary>把账号的 profileDir 解析成绝对路径。</summary>
    /// <param name="account">账号条目。</param>
    /// <returns>profile 绝对路径。</returns>
    public string ResolveProfileDir(ShellAccount account)
    {
        string raw = account.ProfileDir;
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = "auth/" + account.Id;
        }

        if (Path.IsPathRooted(raw))
        {
            return raw;
        }

        return Path.GetFullPath(Path.Combine(_options.DataDir, raw));
    }
}

/// <summary>
/// 账号表文件形态（与主程序共读同写，字段名即契约）。
/// </summary>
public sealed class AccountRegistryFile
{
    /// <summary>最后更新时间（yyyy-MM-dd HH:mm:ss）。</summary>
    public string UpdatedAt { get; set; } = "";

    /// <summary>账号列表。</summary>
    public List<ShellAccount> Accounts { get; set; } = new List<ShellAccount>();
}

/// <summary>
/// 一个账号——显示名 + 自己的浏览器数据目录。
/// </summary>
public sealed class ShellAccount
{
    /// <summary>账号 id（唯一，同时是 profile 目录名的默认来源）。</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名。</summary>
    public string Name { get; set; } = "";

    /// <summary>浏览器数据目录（相对数据根或绝对路径）。</summary>
    public string ProfileDir { get; set; } = "";

    /// <summary>一次性导入源（Chrome 形态用户目录）；空 = 不做导入，或在 profileDir 上就地导入。</summary>
    public string ImportFrom { get; set; } = "";

    /// <summary>是否启用（停用的不出现在面板与套壳列表）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>备注（手机号后四位之类，人看）。</summary>
    public string Note { get; set; } = "";
}
