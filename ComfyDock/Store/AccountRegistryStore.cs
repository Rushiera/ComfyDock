using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using ComfyDock.Models;
using ComfyDock.Shortcut;

namespace ComfyDock.Store;

/// <summary>
/// 账号表读写——软件内置账号源的唯一真相（<c>data\accounts-registry.json</c>）。
/// 首次运行自动从旧的快捷方式目录迁移一次；此后增删改一律走本文件。
/// </summary>
public sealed class AccountRegistryStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _filePath;
    private readonly string _dataDir;
    private readonly object _gate = new object();
    private AccountRegistry _registry = new AccountRegistry();

    /// <summary>以账号表路径与数据根构造（启动即读；文件缺失时按空表）。</summary>
    /// <param name="filePath">账号表完整路径。</param>
    /// <param name="dataDir">数据根目录（用于解析相对的数据目录路径）。</param>
    public AccountRegistryStore(string filePath, string dataDir)
    {
        _filePath = filePath;
        _dataDir = dataDir;
        Load();
    }

    /// <summary>账号表文件完整路径。</summary>
    public string FilePath => _filePath;

    /// <summary>读账号表——文件缺失或损坏时按空表处理。</summary>
    public void Load()
    {
        lock (_gate)
        {
            _registry = ReadFile();
        }
    }

    /// <summary>全部账号（含停用）。</summary>
    /// <returns>账号副本列表。</returns>
    public List<AccountEntry> All()
    {
        lock (_gate)
        {
            return new List<AccountEntry>(_registry.Accounts);
        }
    }

    /// <summary>启用的账号（面板与采集清单都用它）。</summary>
    /// <returns>启用账号副本列表。</returns>
    public List<AccountEntry> Active()
    {
        List<AccountEntry> result = new List<AccountEntry>();
        lock (_gate)
        {
            foreach (AccountEntry entry in _registry.Accounts)
            {
                if (entry.Enabled)
                {
                    result.Add(entry);
                }
            }
        }

        return result;
    }

    /// <summary>按 id 找账号。</summary>
    /// <param name="accountId">账号 id。</param>
    /// <returns>命中返回账号；未命中返回 null。</returns>
    public AccountEntry? Find(string accountId)
    {
        lock (_gate)
        {
            foreach (AccountEntry entry in _registry.Accounts)
            {
                if (string.Equals(entry.Id, accountId, StringComparison.Ordinal))
                {
                    return entry;
                }
            }
        }

        return null;
    }

    /// <summary>把账号的数据目录解析成绝对路径。</summary>
    /// <param name="entry">账号条目。</param>
    /// <returns>数据目录绝对路径。</returns>
    public string ResolveProfileDir(AccountEntry entry)
    {
        string raw = entry.ProfileDir;
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = "auth/" + entry.Id;
        }

        if (Path.IsPathRooted(raw))
        {
            return raw;
        }

        return Path.GetFullPath(Path.Combine(_dataDir, raw));
    }

    /// <summary>把启用账号转成采集 / 打开动作沿用的槽位形态（ShortcutPath = 账号 id，ShortcutName = 显示名）。</summary>
    /// <returns>槽位列表。</returns>
    public List<ShortcutEntry> ToEntries()
    {
        List<ShortcutEntry> list = new List<ShortcutEntry>();
        foreach (AccountEntry entry in Active())
        {
            list.Add(ToEntry(entry));
        }

        return list;
    }

    /// <summary>把单个账号转成槽位形态。</summary>
    /// <param name="entry">账号条目。</param>
    /// <returns>槽位。</returns>
    public ShortcutEntry ToEntry(AccountEntry entry)
    {
        ShortcutEntry slot = new ShortcutEntry();
        slot.ShortcutName = entry.Name.Length == 0 ? entry.Id : entry.Name;
        slot.ShortcutPath = entry.Id;
        slot.ProfileDir = ResolveProfileDir(entry);
        return slot;
    }

    /// <summary>新增一个账号并落盘。</summary>
    /// <param name="name">显示名。</param>
    /// <param name="note">备注。</param>
    /// <param name="importFrom">一次性导入源目录（可空）。</param>
    /// <param name="error">失败原因。</param>
    /// <returns>新建的账号；失败返回 null。</returns>
    public AccountEntry? Add(string name, string note, string importFrom, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "显示名不能为空";
            return null;
        }

        lock (_gate)
        {
            string id = NextId();
            AccountEntry entry = new AccountEntry();
            entry.Id = id;
            entry.Name = name.Trim();
            entry.Note = note;
            entry.ImportFrom = importFrom;
            entry.ProfileDir = "auth/" + id;
            entry.Enabled = true;
            _registry.Accounts.Add(entry);
            SaveLocked();
            return entry;
        }
    }

    /// <summary>改一个账号的字段并落盘。</summary>
    /// <param name="accountId">账号 id。</param>
    /// <param name="name">新显示名（空 = 不改）。</param>
    /// <param name="note">新备注（null = 不改）。</param>
    /// <param name="enabled">启用状态（null = 不改）。</param>
    /// <param name="error">失败原因。</param>
    /// <returns>改后的账号；失败返回 null。</returns>
    public AccountEntry? Update(string accountId, string? name, string? note, bool? enabled, out string error)
    {
        error = "";
        lock (_gate)
        {
            AccountEntry? entry = null;
            foreach (AccountEntry item in _registry.Accounts)
            {
                if (string.Equals(item.Id, accountId, StringComparison.Ordinal))
                {
                    entry = item;
                    break;
                }
            }

            if (entry == null)
            {
                error = "账号不存在：" + accountId;
                return null;
            }

            if (name != null && name.Trim().Length > 0)
            {
                entry.Name = name.Trim();
            }

            if (note != null)
            {
                entry.Note = note;
            }

            if (enabled.HasValue)
            {
                entry.Enabled = enabled.Value;
            }

            SaveLocked();
            return entry;
        }
    }

    /// <summary>删除一个账号条目（只删账，不动磁盘上的数据目录）。</summary>
    /// <param name="accountId">账号 id。</param>
    /// <returns>是否删掉了条目。</returns>
    public bool Remove(string accountId)
    {
        lock (_gate)
        {
            int index = _registry.Accounts.FindIndex(item => string.Equals(item.Id, accountId, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }

            _registry.Accounts.RemoveAt(index);
            SaveLocked();
            return true;
        }
    }

    /// <summary>首次运行迁移——账号表不存在时，从旧的快捷方式目录建档一次。</summary>
    /// <param name="legacyRoot">旧快捷方式目录（含 *.lnk）。</param>
    /// <param name="snapshot">既有快照（用于判定哪些账号曾登录成功）。</param>
    /// <returns>新建的条目数。</returns>
    public int EnsureMigrated(string legacyRoot, AccountSnapshot snapshot)
    {
        lock (_gate)
        {
            if (File.Exists(_filePath))
            {
                return 0;
            }

            List<AccountEntry> built = new List<AccountEntry>();
            if (Directory.Exists(legacyRoot))
            {
                foreach (ShortcutEntry lnk in ShortcutScanner.Scan(legacyRoot))
                {
                    if (lnk.ProfileDir.Length == 0)
                    {
                        continue;
                    }

                    AccountEntry entry = BuildFromShortcut(lnk, snapshot, legacyRoot);
                    built.Add(entry);
                }
            }

            _registry = new AccountRegistry();
            _registry.Accounts = built;
            SaveLocked();
            return built.Count;
        }
    }

    private AccountEntry BuildFromShortcut(ShortcutEntry lnk, AccountSnapshot snapshot, string legacyRoot)
    {
        string id = "acc-" + ProfileNumber(lnk.ProfileDir);
        AccountSlot? slot = null;
        foreach (AccountSlot item in snapshot.Accounts)
        {
            if (string.Equals(item.ProfileDir, lnk.ProfileDir, StringComparison.OrdinalIgnoreCase))
            {
                slot = item;
                break;
            }
        }

        AccountEntry entry = new AccountEntry();
        entry.Id = id;
        entry.ProfileDir = "auth/" + id;
        entry.ImportFrom = ResolveImportSource(lnk.ProfileDir, legacyRoot);
        entry.Name = lnk.ShortcutName;
        if (slot != null)
        {
            if (slot.NickName.Length > 0)
            {
                entry.Name = slot.NickName;
            }

            entry.Note = slot.Mobile;
            entry.Enabled = string.Equals(slot.Status, "ok", StringComparison.Ordinal);
        }
        else
        {
            entry.Enabled = false;
        }

        return entry;
    }

    private static string ResolveImportSource(string lnkProfileDir, string legacyRoot)
    {
        string leaf = Path.GetFileName(lnkProfileDir);
        string? parentDir = Path.GetDirectoryName(lnkProfileDir);
        string parent = parentDir == null ? "" : Path.GetFileName(parentDir);
        if (leaf.Length > 0 && parent.Length > 0)
        {
            string candidate = Path.Combine(legacyRoot, parent, leaf);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return lnkProfileDir;
    }

    private static string ProfileNumber(string profileDir)
    {
        int end = profileDir.Length - 1;
        while (end >= 0 && char.IsDigit(profileDir[end]))
        {
            end = end - 1;
        }

        string digits = profileDir.Substring(end + 1);
        if (digits.Length == 0)
        {
            return "0";
        }

        return digits;
    }

    private string NextId()
    {
        int index = _registry.Accounts.Count + 1;
        while (true)
        {
            string candidate = "acc-" + index;
            bool used = _registry.Accounts.Any(item => string.Equals(item.Id, candidate, StringComparison.Ordinal));
            if (!used)
            {
                return candidate;
            }

            index = index + 1;
        }
    }

    private AccountRegistry ReadFile()
    {
        if (!File.Exists(_filePath))
        {
            return new AccountRegistry();
        }

        try
        {
            string json = File.ReadAllText(_filePath);
            AccountRegistry? loaded = JsonSerializer.Deserialize<AccountRegistry>(json, SerializerOptions);
            if (loaded == null)
            {
                return new AccountRegistry();
            }

            foreach (AccountEntry entry in loaded.Accounts)
            {
                if (string.IsNullOrWhiteSpace(entry.ProfileDir))
                {
                    entry.ProfileDir = "auth/" + entry.Id;
                }
            }

            return loaded;
        }
        catch (JsonException)
        {
            return new AccountRegistry();
        }
    }

    private void SaveLocked()
    {
        _registry.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string? dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(_filePath, JsonSerializer.Serialize(_registry, SerializerOptions));
    }
}
