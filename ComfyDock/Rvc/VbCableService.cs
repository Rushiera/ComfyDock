using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace ComfyDock.Rvc;

/// <summary>
/// VB-CABLE 的状态——驱动装没装、内置安装包在不在位、解压出来的安装器在不在。
/// </summary>
public sealed class VbCableStatus
{
    /// <summary>驱动是否已装（按 System32\drivers 下的 VB 驱动文件判定）。</summary>
    public bool Installed { get; set; }

    /// <summary>命中的驱动文件路径（未装给空串）。</summary>
    public string DriverPath { get; set; } = "";

    /// <summary>内置安装包是否在位。</summary>
    public bool PackageOk { get; set; }

    /// <summary>内置安装包路径（未找到给空串）。</summary>
    public string PackagePath { get; set; } = "";

    /// <summary>内置安装包字节数。</summary>
    public long PackageBytes { get; set; }

    /// <summary>解压后的安装器是否在位。</summary>
    public bool SetupOk { get; set; }

    /// <summary>解压后的安装器路径（未解压给空串）。</summary>
    public string SetupPath { get; set; } = "";

    /// <summary>安装包解压落点。</summary>
    public string WorkDir { get; set; } = "";

    /// <summary>检测时刻。</summary>
    public string CheckedAt { get; set; } = "";
}

/// <summary>
/// VB-CABLE 虚拟声卡的端点辅助——三件事：查驱动装没装、把内置安装包解出来、以管理员权限跑安装器。
/// <para>定位：VB-CABLE 是 VB-Audio 的虚拟音频线（donationware，包原样内置不修改，来源 www.vb-cable.com）——
/// CABLE Input 收到的声音原样转到 CABLE Output，别的软件把 CABLE Output 当麦克风即可拿到变声。
/// 本类只负责「检测 + 安装入口」，不 P/Invoke 驱动、不改音频路由、不碰系统默认设备。</para>
/// </summary>
public sealed class VbCableService
{
    /// <summary>包内 64 位安装器文件名。</summary>
    public const string SetupName = "VBCABLE_Setup_x64.exe";

    /// <summary>安装包文件名通配（版本会变，取目录里最新的一个）。</summary>
    private const string PackagePattern = "VBCABLE_Driver_Pack*.zip";

    /// <summary>旧版安装器直接放 System32\drivers 时的驱动文件名（新版走 DriverStore，见 FindDriverFile）。</summary>
    private static readonly string[] DriverNames =
    {
        "vbaudio_cable64_win10.sys",
        "vbaudio_cable64_arm_win10.sys",
        "vbaudio_cable64_win7.sys",
        "vbaudio_cable64_vista.sys",
        "vbaudio_cable_win7.sys",
        "vbaudio_cable_xp.sys",
    };

    private readonly string _packageDir;
    private readonly string _workDir;

    /// <summary>构造函数。</summary>
    /// <param name="packageDir">内置安装包目录（随软件的 runtime\vbcable）。</param>
    /// <param name="workDir">解压落点（data\vbcable）。</param>
    public VbCableService(string packageDir, string workDir)
    {
        _packageDir = packageDir ?? "";
        _workDir = workDir ?? "";
    }

    /// <summary>检测状态（只读盘，不起进程、不装东西）。</summary>
    /// <returns>状态快照。</returns>
    public VbCableStatus Detect()
    {
        var status = new VbCableStatus
        {
            WorkDir = _workDir,
            CheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        string driverPath = FindDriverFile();
        if (driverPath.Length > 0)
        {
            status.Installed = true;
            status.DriverPath = driverPath;
        }

        try
        {
            if (_packageDir.Length > 0 && Directory.Exists(_packageDir))
            {
                FileInfo? newest = null;
                foreach (FileInfo file in new DirectoryInfo(_packageDir).GetFiles(PackagePattern))
                {
                    if (newest == null || file.LastWriteTimeUtc > newest.LastWriteTimeUtc)
                    {
                        newest = file;
                    }
                }

                if (newest != null)
                {
                    status.PackageOk = true;
                    status.PackagePath = newest.FullName;
                    status.PackageBytes = newest.Length;
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            status.PackagePath = "";
        }

        try
        {
            if (_workDir.Length > 0)
            {
                string setup = Path.Combine(_workDir, SetupName);
                if (File.Exists(setup))
                {
                    status.SetupOk = true;
                    status.SetupPath = setup;
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            status.SetupPath = "";
        }

        return status;
    }

    /// <summary>
    /// 找 VB-CABLE 的驱动文件——装好后实际落在 **DriverStore 的包目录**里（`System32\drivers` 下没有副本，
    /// 2026-10-20 本机实测：`DriverStore\FileRepository\vbmmecable64_win10.inf_amd64_*\vbaudio_cable64_win10.sys`），
    /// 所以先扫 DriverStore，再回落旧版安装器直接放 `System32\drivers` 的情形。
    /// </summary>
    /// <returns>命中的驱动路径（没装返回空串）。</returns>
    private static string FindDriverFile()
    {
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);

        try
        {
            string store = Path.Combine(system, "DriverStore", "FileRepository");
            if (Directory.Exists(store))
            {
                foreach (string dir in Directory.GetDirectories(store, "vb*cable*"))
                {
                    string[] files = Directory.GetFiles(dir, "vbaudio_cable*.sys");
                    if (files.Length > 0)
                    {
                        return files[0];
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // DriverStore 读不到就往下回落，不在这里出声
        }

        string drivers = Path.Combine(system, "drivers");
        for (int i = 0; i < DriverNames.Length; i++)
        {
            string candidate = Path.Combine(drivers, DriverNames[i]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "";
    }

    /// <summary>把内置安装包解到工作目录（已在位则跳过）。</summary>
    /// <param name="setupPath">解出来的安装器路径。</param>
    /// <param name="problem">失败原因。</param>
    /// <returns>可安装返回 true。</returns>
    public bool Prepare(out string setupPath, out string problem)
    {
        setupPath = "";
        problem = "";

        VbCableStatus status = Detect();
        if (status.SetupOk)
        {
            setupPath = status.SetupPath;
            return true;
        }

        if (!status.PackageOk)
        {
            problem = _packageDir.Length == 0
                ? "内置安装包目录未配置"
                : "内置安装包不在位：" + _packageDir + "（缺 VBCABLE_Driver_Pack*.zip）";
            return false;
        }

        if (_workDir.Length == 0)
        {
            problem = "解压落点未配置";
            return false;
        }

        try
        {
            Directory.CreateDirectory(_workDir);
            ZipFile.ExtractToDirectory(status.PackagePath, _workDir, true);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
        {
            problem = "解压内置安装包失败：" + ex.Message;
            return false;
        }

        string candidate = Path.Combine(_workDir, SetupName);
        if (!File.Exists(candidate))
        {
            problem = "安装包里没有 " + SetupName + "（包结构不对）";
            return false;
        }

        setupPath = candidate;
        return true;
    }

    /// <summary>以管理员权限跑安装器（UAC 由系统弹；装完要重启电脑）。</summary>
    /// <returns>(是否已发起, 人话)。</returns>
    public (bool Ok, string Message) Install()
    {
        if (!Prepare(out string setup, out string problem))
        {
            return (false, problem);
        }

        try
        {
            var info = new ProcessStartInfo(setup)
            {
                Verb = "runas",
                Arguments = "-i -h",
                UseShellExecute = true,
                WorkingDirectory = _workDir,
            };
            Process.Start(info);
            return (true, "已请求以管理员权限安装 VB-CABLE——UAC 弹窗点「是」，装完**重启电脑**才会出现 CABLE 端点");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException
            || ex is FileNotFoundException)
        {
            return (false, "提权安装没起来：" + ex.Message + "——可手动双击 " + setup);
        }
    }

    /// <summary>端点名是不是 VB-CABLE 的（CABLE Input = 送声入口 · CABLE Output = 别的软件取声的麦克风）。</summary>
    /// <param name="name">系统端点名。</param>
    /// <returns>是虚拟声卡端点返回 true。</returns>
    public static bool IsCable(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        string upper = name.ToUpperInvariant();
        return upper.Contains("CABLE INPUT") || upper.Contains("CABLE OUTPUT");
    }
}
