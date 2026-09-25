using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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

        // [段2] 命令行参数沿用本次启动（--port / --data 等一并带上）；端口占用交给新实例的启动守卫处理
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = false,
            };

            for (int i = from; i < args.Length; i++)
            {
                psi.ArgumentList.Add(args[i]);
            }

            Process? child = Process.Start(psi);
            if (child is null)
            {
                message = "新实例没有起来（Process.Start 返回空）";
                return false;
            }

            message = "已拉起新实例（进程 " + child.Id + "，映像 " + Path.GetFileName(exePath) + "）";
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
}
