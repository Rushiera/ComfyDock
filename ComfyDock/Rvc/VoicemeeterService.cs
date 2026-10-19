using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace ComfyDock.Rvc;

/// <summary>
/// Voicemeeter 的检测结果——装没装、跑没跑、从哪个 exe 拉起。
/// </summary>
public sealed class VoicemeeterStatus
{
    /// <summary>是否检测到安装。</summary>
    public bool Installed { get; set; }

    /// <summary>安装目录（未装给空串）。</summary>
    public string InstallDir { get; set; } = "";

    /// <summary>首选启动的 exe（未装给空串）。</summary>
    public string ExePath { get; set; } = "";

    /// <summary>目录里可用的 exe 清单（标准版 / Banana / Potato 等）。</summary>
    public List<string> Candidates { get; set; } = new List<string>();

    /// <summary>是否已在运行。</summary>
    public bool Running { get; set; }

    /// <summary>在跑的进程名（未跑给空串）。</summary>
    public string ProcessName { get; set; } = "";

    /// <summary>检测时刻。</summary>
    public string CheckedAt { get; set; } = "";
}

/// <summary>
/// Voicemeeter 的端点辅助——只做三件事：检测安装、看在不在跑、一键拉起。
/// <para>定位：Voicemeeter 只是虚拟声卡端点，控制面留在本软件。不 P/Invoke <c>VoicemeeterRemote64.dll</c>、不改混音台、不装音频驱动。</para>
/// </summary>
public sealed class VoicemeeterService
{
    /// <summary>默认安装目录（VB 家的固定落点）。</summary>
    private const string DefaultInstallDir = @"C:\Program Files (x86)\VB\Voicemeeter";

    /// <summary>进程名里出现这个片段即认为是 Voicemeeter 系（voicemeeter / voicemeeterpro / voicemeeter8 等）。</summary>
    private const string ProcessFragment = "voicemeeter";

    /// <summary>安装目录（可覆盖）。</summary>
    private readonly string _installDir;

    /// <summary>以安装目录构造。</summary>
    /// <param name="installDir">安装目录（空 = 默认落点）。</param>
    public VoicemeeterService(string installDir)
    {
        _installDir = string.IsNullOrWhiteSpace(installDir) ? DefaultInstallDir : installDir;
    }

    /// <summary>安装目录。</summary>
    public string InstallDir
    {
        get { return _installDir; }
    }

    /// <summary>
    /// 判定一个端点名是不是 Voicemeeter 的虚拟声卡（VAIO 系列）。
    /// <para>设备名按系统端点原样传来，这里只做识别标记，不改名。</para>
    /// </summary>
    /// <param name="name">设备名。</param>
    /// <returns>是虚拟声卡返回 true。</returns>
    public static bool IsVaio(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (name.IndexOf("VAIO", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        if (name.IndexOf("VoiceMeeter", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 检测安装与运行态（只看磁盘与进程表，不启动任何东西）。
    /// </summary>
    /// <returns>检测结果。</returns>
    public VoicemeeterStatus Detect()
    {
        var status = new VoicemeeterStatus();
        status.CheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        status.InstallDir = _installDir;

        // [段1] 扫安装目录里的 exe——标准版 / Banana / Potato 各自一个
        if (Directory.Exists(_installDir))
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(_installDir, "voicemeeter*.exe", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                files = new string[0];
            }

            List<string> candidates = new List<string>();
            for (int i = 0; i < files.Length; i++)
            {
                candidates.Add(files[i]);
            }

            candidates.Sort(StringComparer.OrdinalIgnoreCase);
            status.Candidates = candidates;
            status.Installed = candidates.Count > 0;

            string preferred = "";
            for (int i = 0; i < candidates.Count; i++)
            {
                string file = candidates[i];
                if (string.Equals(Path.GetFileName(file), "voicemeeter.exe", StringComparison.OrdinalIgnoreCase))
                {
                    preferred = file;
                    break;
                }
            }

            if (preferred.Length == 0 && candidates.Count > 0)
            {
                preferred = candidates[0];
            }

            status.ExePath = preferred;
        }

        // [段2] 进程表——名字里带片段即认定为在跑
        string runningName = FindRunningProcess();
        status.Running = runningName.Length > 0;
        status.ProcessName = runningName;
        return status;
    }

    /// <summary>
    /// 一键拉起 Voicemeeter（独立进程，本软件退出不连带杀）。
    /// </summary>
    /// <returns>(是否成功, 人话)。</returns>
    public (bool Ok, string Message) Launch()
    {
        VoicemeeterStatus status = Detect();
        if (!status.Installed)
        {
            return (false, "没找到 Voicemeeter 安装：" + _installDir);
        }

        if (status.Running)
        {
            return (true, "Voicemeeter 已在运行（" + status.ProcessName + "）");
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = status.ExePath,
                WorkingDirectory = _installDir,
                UseShellExecute = true,
            };
            Process.Start(psi);
        }
        catch (Exception ex) when (ex is IOException || ex is Win32Exception || ex is InvalidOperationException)
        {
            return (false, "拉起 Voicemeeter 失败：" + ex.Message);
        }

        return (true, "已拉起 " + Path.GetFileName(status.ExePath) + "（首次启动需系统音频驱动就绪，约 3~5 秒）");
    }

    /// <summary>
    /// 在进程表里找 Voicemeeter 系进程。
    /// </summary>
    /// <returns>进程名（没找到给空串）。</returns>
    private static string FindRunningProcess()
    {
        Process[] all;
        try
        {
            all = Process.GetProcesses();
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception)
        {
            return "";
        }

        string found = "";
        for (int i = 0; i < all.Length; i++)
        {
            Process proc = all[i];
            try
            {
                string name = proc.ProcessName;
                if (name.IndexOf(ProcessFragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = name;
                    break;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception)
            {
                continue;
            }
            finally
            {
                proc.Dispose();
            }
        }

        return found;
    }
}
