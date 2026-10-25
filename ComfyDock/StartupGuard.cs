using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;

namespace ComfyDock;

/// <summary>启动守卫——面板端口被占用时，先清掉自己的旧实例（含换版发布期间改名成 `.old` 的那一代），再等端口回收；占用者不是本程序时明确报错退出，不越权结束别人的进程。</summary>
public static class StartupGuard
{
    /// <summary>释放目标端口——必要时结束旧实例并等待端口回收。</summary>
    /// <param name="port">面板端口。</param>
    /// <returns>守卫结果（Ok = false 时调用方应退出）。</returns>
    public static StartupGuardResult Release(int port)
    {
        var result = new StartupGuardResult();
        List<int> owners = FindListeners(port);
        if (owners.Count == 0)
        {
            return result;
        }

        // [段1] 只结束自己人——占用者是别的程序时如实报错
        string self = Process.GetCurrentProcess().ProcessName;
        var foreign = new List<string>();

        foreach (int pid in owners)
        {
            string name = ReadProcessName(pid);
            if (name.Length == 0)
            {
                continue;
            }

            if (!IsOwnInstance(name, self))
            {
                foreign.Add(name + "（进程 " + pid + "）");
                continue;
            }

            // 旧实例可能正在自行退出（面板「重启」= 先起新实例、旧实例响应落地后才停机）——
            // 先等它走完自然退出，避免无谓强杀（它开着的浏览器窗口、在途落盘都还在）
            if (WaitReleased(port, 15))
            {
                result.Message = "旧实例已自行退出，端口 " + port + " 已回收";
                return result;
            }

            if (TryKill(pid))
            {
                result.Killed = result.Killed + 1;
            }
        }

        if (foreign.Count > 0)
        {
            result.Ok = false;
            result.Message = "端口 " + port + " 被其它程序占用：" + string.Join("、", foreign)
                + "——本程序只结束自己的旧实例，请先关掉它或换 --port";
            return result;
        }

        // [段2] 等端口回收——旧实例退出后监听套接字需要一点时间才消失
        if (WaitReleased(port, 30))
        {
            result.Message = "已结束旧实例 " + result.Killed + " 个，端口 " + port + " 已回收";
            return result;
        }

        result.Ok = false;
        result.Message = "端口 " + port + " 在结束旧实例后仍被占用（等待 3 秒超时）";
        return result;
    }
    /// <summary>等端口自然释放——每 100 毫秒看一眼，看见空即返回。</summary>
    /// <param name="port">面板端口。</param>
    /// <param name="steps">最多等待的步数（每步 100 毫秒）。</param>
    /// <returns>是否在等待窗口内释放。</returns>
    private static bool WaitReleased(int port, int steps)
    {
        for (int i = 0; i < steps; i++)
        {
            Thread.Sleep(100);
            if (FindListeners(port).Count == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>列出正在监听指定端口的进程 id。</summary>
    /// <param name="port">端口号。</param>
    /// <returns>进程 id 列表（不含无效项）。</returns>
    private static List<int> FindListeners(int port)
    {
        var result = new List<int>();
        string suffix = ":" + port;

        var psi = new ProcessStartInfo
        {
            FileName = "netstat.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-ano");
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("TCP");

        using Process? shell = Process.Start(psi);
        if (shell is null)
        {
            return result;
        }

        string output = shell.StandardOutput.ReadToEnd();
        shell.WaitForExit(5000);

        string[] lines = output.Split('\n');
        foreach (string raw in lines)
        {
            string[] parts = raw.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5)
            {
                continue;
            }

            if (!string.Equals(parts[0], "TCP", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!parts[1].EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            // 🔴 只认 LISTENING——旧实例退出后它的监听套接字会在 netstat 里留一段 FIN_WAIT_2 / TIME_WAIT，
            // 那不是「有人占着端口」，随即就会消失。把这类行当占用会让新实例等满窗口后自我拒绝启动，
            // 而旧实例已按排程停机 → 换版 / 重启后两个进程全消失（2026-10-25 实测：新实例 51712
            // 起来 40 秒内退出、旧实例 52548 同时消失、8971 只剩 FIN_WAIT_2、事件日志无崩溃记录）。
            if (!string.Equals(parts[3], "LISTENING", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!int.TryParse(parts[4], out int pid) || pid <= 0)
            {
                continue;
            }

            // 同一进程常有多条监听记录（IPv4 / IPv6 各一行）——去重后再报，免得错误提示里同一 pid 重复四遍
            if (!result.Contains(pid))
            {
                result.Add(pid);
            }
        }

        return result;
    }

    /// <summary>读进程名（进程已退出返回空串）。</summary>
    /// <param name="pid">进程 id。</param>
    /// <returns>进程名；读不到返回空串。</returns>
    private static string ReadProcessName(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return "";
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }

    /// <summary>结束旧实例进程并等它退出——只杀这一个进程，不杀进程树（原因见方法内注释）。</summary>
    /// <param name="pid">进程 id。</param>
    /// <returns>是否成功结束。</returns>
    private static bool TryKill(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);

            // 只杀面板进程本身，不用 entireProcessTree：
            // ① 重启链里新实例是旧实例的子进程——杀树会把正在执行守卫的自己一并带走（两个进程全消失）
            // ② 旧实例名下的浏览器窗口（受控实例 / headless 临时实例）按设计不跟着面板一起死
            process.Kill();
            process.WaitForExit(5000);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
    /// <summary>判断占用端口的进程是不是自己人——两边都先归一成基名再比（见 NormalizeName）：换版期间旧实例映像被改名成 `ComfyDock.exe.old` 后，`Process.ProcessName` 会返回带 `.exe` 的 `ComfyDock.exe`，与新实例的 `ComfyDock` 直接比会判成「别的程序」→ 新实例拒绝启动、旧实例又已停机 → 两个进程全消失（2026-10-18 实测复现）。</summary>
    /// <param name="name">占用进程的进程名。</param>
    /// <param name="self">本进程的进程名。</param>
    /// <returns>是否属于本程序的实例。</returns>
    private static bool IsOwnInstance(string name, string self)
    {
        return string.Equals(NormalizeName(name), NormalizeName(self), StringComparison.OrdinalIgnoreCase);
    }
    /// <summary>
    /// 把进程名归一成可比的基名——换版期间映像被改名成 `ComfyDock.exe.old` 时，
    /// `Process.ProcessName` 会把 `.old` 当扩展名、返回带 `.exe` 的 `ComfyDock.exe`；
    /// 正常映像返回不带扩展名的 `ComfyDock`。两层后缀一并剥掉才比得齐（2026-10-18 实测根因）。
    /// </summary>
    /// <param name="name">进程名。</param>
    /// <returns>基名。</returns>
    private static string NormalizeName(string name)
    {
        string value = name;
        bool stripped = true;
        while (stripped)
        {
            stripped = false;
            if (value.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(0, value.Length - 4);
                stripped = true;
            }
            else if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(0, value.Length - 4);
                stripped = true;
            }
        }

        return value;
    }
}

/// <summary>启动守卫结果。</summary>
public sealed class StartupGuardResult
{
    /// <summary>是否放行（false = 端口被别的程序占用或回收超时）。</summary>
    public bool Ok { get; set; } = true;

    /// <summary>结果说明（空 = 端口本就空闲）。</summary>
    public string Message { get; set; } = "";

    /// <summary>本次结束的旧实例数。</summary>
    public int Killed { get; set; }
}
