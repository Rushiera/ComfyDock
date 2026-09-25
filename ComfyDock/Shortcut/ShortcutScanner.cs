using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ComfyDock.Shortcut;

/// <summary>
/// 快捷方式扫描——把目录下的 *.lnk 解析成账号槽位（快捷方式路径 + 浏览器用户目录）。
/// </summary>
public static class ShortcutScanner
{
    private const string UserDataDirFlag = "--user-data-dir=";

    /// <summary>
    /// 扫描指定目录下全部 *.lnk（不递归），按用户目录名自然序排序返回。
    /// </summary>
    /// <param name="googleRoot">快捷方式所在目录（如 D:\done\google）。</param>
    /// <returns>槽位列表；目录不存在时返回空列表。</returns>
    public static List<ShortcutEntry> Scan(string googleRoot)
    {
        var list = new List<ShortcutEntry>();
        if (!Directory.Exists(googleRoot))
        {
            return list;
        }

        foreach (string lnk in Directory.GetFiles(googleRoot, "*.lnk", SearchOption.TopDirectoryOnly))
        {
            list.Add(new ShortcutEntry
            {
                ShortcutName = Path.GetFileName(lnk),
                ShortcutPath = lnk,
                ProfileDir = ExtractUserDataDir(lnk) ?? "",
            });
        }

        list.Sort(CompareByProfile);
        return list;
    }

    /// <summary>
    /// 从 lnk 字节流提取 --user-data-dir 参数值——直接扫 UTF-16LE 序列，零依赖。
    /// </summary>
    /// <param name="lnkPath">快捷方式完整路径。</param>
    /// <returns>用户目录路径；未命中返回 null。</returns>
    public static string? ExtractUserDataDir(string lnkPath)
    {
        byte[] bytes = File.ReadAllBytes(lnkPath);
        byte[] needle = Encoding.Unicode.GetBytes(UserDataDirFlag);
        int hit = IndexOf(bytes, needle);
        if (hit < 0)
        {
            return null;
        }

        string raw = ReadCountedString(bytes, hit);
        int flag = raw.IndexOf(UserDataDirFlag, StringComparison.Ordinal);
        if (flag < 0)
        {
            return null;
        }

        return raw.Substring(flag + UserDataDirFlag.Length).Trim();
    }
    /// <summary>按 lnk 的 StringData 规则读串——命中点前 2 字节为字符数前缀，其后为 UTF-16 数据（无结尾 NUL）。</summary>
    /// <param name="bytes">lnk 全部字节。</param>
    /// <param name="start">字符串首字符所在偏移。</param>
    /// <returns>读出的字符串；前缀不可信时回落到读到 NUL 为止。</returns>
    private static string ReadCountedString(byte[] bytes, int start)
    {
        int count = start >= 2 ? (bytes[start - 2] | (bytes[start - 1] << 8)) : -1;

        if (count <= 0 || count > 4096)
        {
            var fallback = new StringBuilder();
            for (int i = start; i + 1 < bytes.Length; i += 2)
            {
                char ch = (char)(bytes[i] | (bytes[i + 1] << 8));
                if (ch == '\0')
                {
                    break;
                }
                fallback.Append(ch);
            }
            return fallback.ToString();
        }

        var sb = new StringBuilder(count);
        for (int i = 0; i < count; i++)
        {
            int p = start + i * 2;
            if (p + 1 >= bytes.Length)
            {
                break;
            }
            sb.Append((char)(bytes[p] | (bytes[p + 1] << 8)));
        }
        return sb.ToString();
    }

    private static int CompareByProfile(ShortcutEntry a, ShortcutEntry b)
    {
        int ra = TrailingNumber(a.ProfileDir);
        int rb = TrailingNumber(b.ProfileDir);
        if (ra != rb)
        {
            return ra.CompareTo(rb);
        }
        return string.CompareOrdinal(a.ShortcutName, b.ShortcutName);
    }

    private static int TrailingNumber(string path)
    {
        int end = path.Length - 1;
        while (end >= 0 && char.IsDigit(path[end]))
        {
            end--;
        }
        string digits = path.Substring(end + 1);
        return int.TryParse(digits, out int value) ? value : int.MaxValue;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        int limit = haystack.Length - needle.Length;
        for (int i = 0; i <= limit; i++)
        {
            bool matched = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    matched = false;
                    break;
                }
            }
            if (matched)
            {
                return i;
            }
        }
        return -1;
    }
}

/// <summary>
/// 快捷方式槽位——尚未采集的原始映射（快捷方式 ↔ 浏览器用户目录）。
/// </summary>
public sealed class ShortcutEntry
{
    /// <summary>快捷方式文件名。</summary>
    public string ShortcutName { get; set; } = "";

    /// <summary>快捷方式完整路径。</summary>
    public string ShortcutPath { get; set; } = "";

    /// <summary>浏览器用户数据目录；lnk 未含该参数时为空串。</summary>
    public string ProfileDir { get; set; } = "";
}
