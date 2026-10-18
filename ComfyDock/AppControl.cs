using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace ComfyDock;

/// <summary>面板电源控制——重启（从换版位拉起一个新的自身实例，本进程随后退出）与关闭（优雅停机）。退出走延迟排程：先让 HTTP 响应落地，再停应用。</summary>
public static class AppControl
{
    /// <summary>响应落地与进程退出之间的等待（毫秒）。</summary>
    private const int StopDelayMs = 700;
    /// <summary>换版位文件名——发布总是把新版本顶到这个名字上（见 design-ComfyDock §0.5「换版发布」）。</summary>
    private const string ExeFileName = "ComfyDock.exe";

    /// <summary>
    /// 起一个新的自身实例——命令行参数沿用本次启动（--port / --data 等一并带上）。
    /// 端口占用交给新实例的启动守卫处理（旧实例这时可能还没退干净）。
    /// </summary>
    /// <param name="message">成功说明或失败原因。</param>
    /// <returns>是否已成功拉起新实例。</returns>
    public static bool TryStartNewInstance(out string message)
    {
        string? selfPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(selfPath))
        {
            message = "拿不到自身程序路径，无法重启";
            return false;
        }

        string[] args = Environment.GetCommandLineArgs();

        // [段1] 换版位 = 程序目录下的固定文件名——发布时旧映像先改名让位、新版本顶到这个位置，
        //        所以这里按名字寻址，不用 Environment.ProcessPath：映像被改名后它可能指回旧文件，
        //        会静默拉起旧代码。框架依赖方式运行（dotnet xxx.dll）没有换版位，沿用自身路径。
        bool hosted = string.Equals(Path.GetFileNameWithoutExtension(selfPath), "dotnet", StringComparison.OrdinalIgnoreCase);
        string exePath = selfPath;
        int from = 1;
        if (hosted)
        {
            from = 0;
        }
        else
        {
            exePath = Path.Combine(AppContext.BaseDirectory, ExeFileName);
            if (!File.Exists(exePath))
            {
                message = "换版位缺失：" + exePath + "——发布未完成或文件已被移走，本次重启取消";
                return false;
            }
        }

        // [段2] 用原生 CreateProcess + CREATE_NEW_CONSOLE 起新实例——🔴 新进程必须**自带一个新控制台**：
        // 继承本进程控制台时，本进程一退出（重启 = 700 ms 后停机）那个控制台随之关闭，
        // Windows 把挂在同一控制台上的新实例一并结束（现象：回执「已拉起新实例」→ 随后端口无人监听、
        // 进程表里也没有新实例）。实测（2026-10-18）三条路只有这条活：
        // ① Process.Start（UseShellExecute=false）→ 继承控制台，重启后必死；
        // ② UseShellExecute=true / `cmd /c start`（隐藏窗口）→ 仍挂在同一个控制台上，重启后同样死；
        // ③ CreateProcess + CREATE_NEW_CONSOLE → 系统新开控制台，连点重启都活着。
        try
        {
            string commandLine = "\"" + exePath + "\"";
            for (int i = from; i < args.Length; i++)
            {
                string arg = args[i];
                commandLine += " " + (arg.IndexOf(' ') >= 0 ? "\"" + arg + "\"" : arg);
            }

            var startup = new StartupInfo();
            startup.cb = Marshal.SizeOf<StartupInfo>();

            bool started = CreateProcess(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CreateNewConsole,
                IntPtr.Zero,
                AppContext.BaseDirectory,
                ref startup,
                out ProcessInformation process);

            if (!started)
            {
                message = "拉起新实例失败：CreateProcess 错误码 " + Marshal.GetLastWin32Error();
                return false;
            }

            // 句柄用完即关——进程本身继续跑，句柄不关会漏
            CloseHandle(process.hProcess);
            CloseHandle(process.hThread);

            message = "已拉起新实例（进程 " + process.dwProcessId + "，映像 " + Path.GetFileName(exePath) + " · 新开控制台）";
            return true;
        }
        catch (Exception ex)
        {
            message = "拉起新实例失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>延迟停机——响应先落地，再走优雅退出。</summary>
    /// <param name="lifetime">应用生命周期。</param>
    /// <returns>停机任务。</returns>
    public static async Task ScheduleStopAsync(IHostApplicationLifetime lifetime)
    {
        await Task.Delay(StopDelayMs).ConfigureAwait(false);
        lifetime.StopApplication();
    }
    /// <summary>读本实例的版本号——程序集信息版本优先（带源修订号时截到 + 之前），读不到回落程序集版本。</summary>
    /// <returns>版本号；都读不到返回空串。</returns>
    private static string ReadVersion()
    {
        AssemblyInformationalVersionAttribute? attr = typeof(AppControl).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (attr is not null)
        {
            string raw = attr.InformationalVersion;
            int plus = raw.IndexOf('+');
            if (plus >= 0)
            {
                raw = raw.Substring(0, plus);
            }

            if (raw.Length > 0)
            {
                return raw;
            }
        }

        Version? ver = typeof(AppControl).Assembly.GetName().Version;
        if (ver is null)
        {
            return "";
        }

        return ver.ToString();
    }
    /// <summary>
    /// 本实例的版本自证——面板显示「跑的是哪一版、什么时候起的」，
    /// 并判断换版位上的文件是否已比本进程新（有新版本等待换版）。
    /// </summary>
    /// <returns>版本 / 进程号 / 启动时刻 / 映像路径 / 换版位路径与时间戳 / 是否待换版。</returns>
    public static object Describe()
    {
        DateTime startedAt = DateTime.MinValue;
        try
        {
            startedAt = Process.GetCurrentProcess().StartTime;
        }
        catch (Exception)
        {
            startedAt = DateTime.MinValue;
        }

        // [段1] 换版位时间戳晚于本进程启动时刻 = 本进程起来之后有新版本落位——读不到就留空、不提示
        string slotPath = Path.Combine(AppContext.BaseDirectory, ExeFileName);
        string slotStamp = "";
        bool slotNewer = false;

        FileInfo slot = new FileInfo(slotPath);
        if (slot.Exists)
        {
            slotStamp = slot.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
            if (startedAt != DateTime.MinValue && slot.LastWriteTime > startedAt)
            {
                slotNewer = true;
            }
        }

        string startedText = "";
        if (startedAt != DateTime.MinValue)
        {
            startedText = startedAt.ToString("yyyy-MM-dd HH:mm:ss");
        }

        string? imagePath = Environment.ProcessPath;
        if (imagePath is null)
        {
            imagePath = "";
        }

        return new
        {
            version = ReadVersion(),
            pid = Environment.ProcessId,
            startedAt = startedText,
            imagePath,
            slotPath,
            slotStamp,
            slotNewer,
        };
    }
    /// <summary>CreateProcess 启动标志——新进程另开一个控制台，不继承本进程的（重启链的唯一正解）。</summary>
    private const uint CreateNewConsole = 0x00000010;
    /// <summary>STARTUPINFO（kernel32 结构体——字段顺序与 Win32 一致，不能删字段）。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        /// <summary>结构体字节数（调用前必须填）。</summary>
        public int cb;

        /// <summary>保留字段。</summary>
        public string? lpReserved;

        /// <summary>桌面名（空 = 继承）。</summary>
        public string? lpDesktop;

        /// <summary>控制台窗口标题。</summary>
        public string? lpTitle;

        /// <summary>窗口位置 X。</summary>
        public int dwX;

        /// <summary>窗口位置 Y。</summary>
        public int dwY;

        /// <summary>窗口宽。</summary>
        public int dwXSize;

        /// <summary>窗口高。</summary>
        public int dwYSize;

        /// <summary>字符缓冲区宽。</summary>
        public int dwXCountChars;

        /// <summary>字符缓冲区高。</summary>
        public int dwYCountChars;

        /// <summary>填充属性。</summary>
        public int dwFillAttribute;

        /// <summary>启动标志。</summary>
        public int dwFlags;

        /// <summary>显示方式。</summary>
        public short wShowWindow;

        /// <summary>保留。</summary>
        public short cbReserved2;

        /// <summary>保留。</summary>
        public IntPtr lpReserved2;

        /// <summary>标准输入句柄。</summary>
        public IntPtr hStdInput;

        /// <summary>标准输出句柄。</summary>
        public IntPtr hStdOutput;

        /// <summary>标准错误句柄。</summary>
        public IntPtr hStdError;
    }
    /// <summary>PROCESS_INFORMATION（kernel32 结构体）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        /// <summary>进程句柄。</summary>
        public IntPtr hProcess;

        /// <summary>主线程句柄。</summary>
        public IntPtr hThread;

        /// <summary>进程 id。</summary>
        public int dwProcessId;

        /// <summary>主线程 id。</summary>
        public int dwThreadId;
    }
    /// <summary>起一个进程（kernel32 原生入口——只有它能指定「另开控制台」）。</summary>
    /// <param name="lpApplicationName">可执行文件（空 = 从命令行解析）。</param>
    /// <param name="lpCommandLine">完整命令行。</param>
    /// <param name="lpProcessAttributes">进程安全属性（空 = 默认）。</param>
    /// <param name="lpThreadAttributes">线程安全属性（空 = 默认）。</param>
    /// <param name="bInheritHandles">是否继承句柄。</param>
    /// <param name="dwCreationFlags">创建标志。</param>
    /// <param name="lpEnvironment">环境块（空 = 继承）。</param>
    /// <param name="lpCurrentDirectory">工作目录。</param>
    /// <param name="lpStartupInfo">启动信息。</param>
    /// <param name="lpProcessInformation">进程信息（出参）。</param>
    /// <returns>是否成功。</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfo lpStartupInfo,
        out ProcessInformation lpProcessInformation);
    /// <summary>关句柄（进程起来后把手柄放掉，进程本身继续跑）。</summary>
    /// <param name="handle">句柄。</param>
    /// <returns>是否成功。</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
