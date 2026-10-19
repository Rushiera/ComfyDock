using System;
using System.Collections.Generic;
using System.IO;

namespace ComfyDock.Shell;

/// <summary>
/// profile 迁移——把 Chrome 形态的用户目录（<c>Default\Network\Cookies</c> + <c>Local State</c>）
/// 导入 WebView2 的数据目录（<c>EBWebView</c> 子目录），使既有登录态可被复用。
/// 只导入凭据与密钥两件，不动缓存；源目录只读、不写、不删。
/// </summary>
public static class ProfileMigrator
{
    /// <summary>导入结果。</summary>
    public sealed class Result
    {
        /// <summary>是否真的导入了文件。</summary>
        public bool Imported { get; set; }

        /// <summary>已复制的文件名。</summary>
        public List<string> Copied { get; } = new List<string>();

        /// <summary>跳过项与原因。</summary>
        public List<string> Skipped { get; } = new List<string>();

        /// <summary>一句话结论。</summary>
        public string Note { get; set; } = "";
    }

    /// <summary>把 Chrome 形态目录里的登录数据导入 WebView2 目录。</summary>
    /// <param name="chromeDir">Chrome 形态用户目录（含 <c>Default</c> 与 <c>Local State</c>）。</param>
    /// <param name="userDataFolder">WebView2 用户数据目录（其下会建 <c>EBWebView</c>）。</param>
    /// <returns>导入结果。</returns>
    public static Result Import(string chromeDir, string userDataFolder)
    {
        Result result = new Result();
        string inner = Path.Combine(userDataFolder, "EBWebView");
        string cookieTarget = Path.Combine(inner, "Default", "Network", "Cookies");

        if (File.Exists(cookieTarget))
        {
            result.Note = "目标已是 WebView2 数据目录——跳过导入";
            return result;
        }

        string cookieSource = Path.Combine(chromeDir, "Default", "Network", "Cookies");
        if (!File.Exists(cookieSource))
        {
            result.Note = "源不是 Chrome 形态（缺 Default\\Network\\Cookies）——按新 profile 处理";
            return result;
        }

        string? cookieDir = Path.GetDirectoryName(cookieTarget);
        if (!string.IsNullOrEmpty(cookieDir))
        {
            Directory.CreateDirectory(cookieDir);
        }

        CopyFile(cookieSource, cookieTarget, result);

        string journalSource = cookieSource + "-journal";
        if (File.Exists(journalSource))
        {
            CopyFile(journalSource, cookieTarget + "-journal", result);
        }

        string stateSource = Path.Combine(chromeDir, "Local State");
        if (File.Exists(stateSource))
        {
            CopyFile(stateSource, Path.Combine(inner, "Local State"), result);
        }
        else
        {
            result.Skipped.Add("Local State（缺失——cookie 密钥不可用，登录态读不出来）");
        }

        result.Imported = result.Copied.Count > 0;
        result.Note = result.Imported ? "已导入 " + result.Copied.Count + " 个文件" : "无可导入文件";
        return result;
    }

    private static void CopyFile(string source, string target, Result result)
    {
        try
        {
            File.Copy(source, target, true);
            result.Copied.Add(Path.GetFileName(source));
        }
        catch (IOException ex)
        {
            result.Skipped.Add(Path.GetFileName(source) + "：" + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            result.Skipped.Add(Path.GetFileName(source) + "：" + ex.Message);
        }
    }
}
