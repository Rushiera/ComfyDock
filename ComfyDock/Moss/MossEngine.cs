using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Moss;

/// <summary>
/// 引擎启动器——按「显存使用方式」重启本机 ComfyUI（对应教程里的按需放进显卡 / 逐层搬运 / 全部常驻）。
/// 只结束**本安装目录下**的 python 进程，别的进程一概不碰；起不来说实话，不假装已运行。
/// </summary>
public static class MossEngine
{
    /// <summary>端口就绪轮询上限（秒）。</summary>
    private const int WaitSeconds = 180;

    /// <summary>HTTP 客户端（探活）。</summary>
    private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

    /// <summary>把显存模式转成 ComfyUI 的启动参数。</summary>
    /// <param name="mode">lowvram / normalvram / highvram。</param>
    /// <returns>参数；不认识的模式返回空串。</returns>
    public static string FlagOf(string mode)
    {
        if (string.Equals(mode, "lowvram", StringComparison.OrdinalIgnoreCase))
        {
            return "--lowvram";
        }

        if (string.Equals(mode, "highvram", StringComparison.OrdinalIgnoreCase))
        {
            return "--highvram";
        }

        // 按需放进显卡 = ComfyUI 的默认行为，**不加参数**——0.41 已把 --normalvram 从参数表里去掉
        // （实测：传 --normalvram 直接 `error: unrecognized arguments`，引擎起不来）
        if (string.Equals(mode, "normalvram", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        return "?";
    }

    /// <summary>按指定显存模式重启引擎。</summary>
    /// <param name="comfyRoot">ComfyUI 根目录。</param>
    /// <param name="url">ComfyUI 地址。</param>
    /// <param name="mode">显存模式。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否就绪, 说明)。</returns>
    public static async Task<(bool Ok, string Message)> RestartAsync(string comfyRoot, string url, string mode, CancellationToken ct)
    {
        string flag = FlagOf(mode);
        if (flag == "?")
        {
            return (false, "显存模式只认 lowvram / normalvram / highvram");
        }

        string installRoot = Path.GetDirectoryName(comfyRoot.TrimEnd('\\', '/')) ?? comfyRoot;
        string python = Path.Combine(installRoot, "python_embeded", "python.exe");
        if (!File.Exists(python))
        {
            return (false, "找不到引擎解释器：" + python + "（本机不是便携整合包？改用手工启动）");
        }

        int killed = StopEngine(installRoot);
        await Task.Delay(1200, ct).ConfigureAwait(false);

        string arguments = "-s ComfyUI\\main.py --windows-standalone-build";
        if (flag.Length > 0)
        {
            arguments += " " + flag;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = python,
                Arguments = arguments,
                WorkingDirectory = installRoot,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is IOException || ex is System.ComponentModel.Win32Exception)
        {
            return (false, "启动失败：" + ex.Message);
        }

        string label = flag.Length > 0 ? flag : "默认（按需放进显卡）";
        for (int i = 0; i < WaitSeconds; i++)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            if (await AliveAsync(url, ct).ConfigureAwait(false))
            {
                return (true, "已按 " + label + " 重启（结束旧进程 " + killed + " 个）");
            }
        }

        return (false, "已执行启动命令，但 " + WaitSeconds + " 秒内端口没起来（看那个新开的控制台窗口）");
    }

    /// <summary>结束本安装目录下的引擎进程（按主模块路径前缀匹配，别的 python 不碰）。</summary>
    /// <param name="installRoot">安装根目录。</param>
    /// <returns>结束的进程数。</returns>
    private static int StopEngine(string installRoot)
    {
        int killed = 0;
        Process[] all;
        try
        {
            all = Process.GetProcesses();
        }
        catch (InvalidOperationException)
        {
            return 0;
        }

        foreach (Process process in all)
        {
            try
            {
                if (!string.Equals(process.ProcessName, "python", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(process.ProcessName, "pythonw", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? path = process.MainModule?.FileName;
                if (path is null || !path.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                process.Kill(true);
                process.WaitForExit(5000);
                killed++;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException)
            {
                // 拿不到主模块（权限 / 已退出）——跳过，不硬闯
                Console.Error.WriteLine("跳过引擎进程 " + process.Id + "：" + ex.Message);
            }
            finally
            {
                process.Dispose();
            }
        }

        return killed;
    }

    /// <summary>探活。</summary>
    /// <param name="url">地址。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>有响应返回 true。</returns>
    private static async Task<bool> AliveAsync(string url, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await Client.GetAsync(url, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is UriFormatException)
        {
            return false;
        }
    }
}
