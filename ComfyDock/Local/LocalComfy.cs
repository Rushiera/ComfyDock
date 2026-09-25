using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Local;

/// <summary>本地 ComfyUI 运行状态快照。</summary>
public sealed class LocalComfyStatus
{
    /// <summary>端口是否在跑（拿到 HTTP 响应即算运行中）。</summary>
    public bool Running { get; set; }

    /// <summary>本地界面地址。</summary>
    public string Url { get; set; } = "";

    /// <summary>本次探测时刻（yyyy-MM-dd HH:mm:ss）。</summary>
    public string CheckedAt { get; set; } = "";

    /// <summary>状态说明 / 失败原因。</summary>
    public string Message { get; set; } = "";
}

/// <summary>
/// 本地 ComfyUI 入口——只做两件事：探测端口状态、一键启动（并等端口就绪）。
/// 不介入 ComfyUI 内部，不做模型 / 队列管理。
/// </summary>
public sealed class LocalComfy
{
    /// <summary>单次探测超时（毫秒）。</summary>
    private const int ProbeTimeoutMs = 1500;

    /// <summary>探测专用 HTTP 客户端（复用——避免每秒新建耗端口）。</summary>
    private static readonly HttpClient ProbeClient = new HttpClient();

    /// <summary>本地界面地址。</summary>
    private readonly string _url;

    /// <summary>启动脚本完整路径。</summary>
    private readonly string _launcherPath;

    /// <summary>启动互斥——同一时刻只允许一次启动在途。</summary>
    private readonly SemaphoreSlim _launchGate = new SemaphoreSlim(1, 1);

    /// <summary>以地址与启动脚本构造。</summary>
    /// <param name="url">本地界面地址。</param>
    /// <param name="launcherPath">启动脚本完整路径。</param>
    public LocalComfy(string url, string launcherPath)
    {
        _url = url;
        _launcherPath = launcherPath;
    }

    /// <summary>探测本地 ComfyUI 是否在跑。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>状态快照。</returns>
    public async Task<LocalComfyStatus> ProbeAsync(CancellationToken ct)
    {
        var status = new LocalComfyStatus
        {
            Url = _url,
            CheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        // [段1] 只有连不上才算未运行——任何 HTTP 响应都说明端口上有服务
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeoutMs);
            using HttpResponseMessage response = await ProbeClient.GetAsync(_url, timeout.Token).ConfigureAwait(false);
            status.Running = true;
            status.Message = "本地 ComfyUI 运行中（HTTP " + (int)response.StatusCode + "）";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            status.Running = false;
            status.Message = "本地 ComfyUI 未运行（探测超时）";
        }
        catch (HttpRequestException)
        {
            status.Running = false;
            status.Message = "本地 ComfyUI 未运行";
        }

        return status;
    }

    /// <summary>启动本地 ComfyUI，并等端口就绪。</summary>
    /// <param name="waitSeconds">等待端口就绪的秒数上限。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>启动后的状态快照。</returns>
    public async Task<LocalComfyStatus> LaunchAsync(int waitSeconds, CancellationToken ct)
    {
        await _launchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            LocalComfyStatus current = await ProbeAsync(ct).ConfigureAwait(false);
            if (current.Running)
            {
                return current;
            }

            // [段1] 执行启动脚本——脚本自己决定窗口形态，工作目录设为脚本所在目录
            if (!File.Exists(_launcherPath))
            {
                current.Message = "启动脚本不存在：" + _launcherPath;
                return current;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = _launcherPath,
                WorkingDirectory = Path.GetDirectoryName(_launcherPath),
                UseShellExecute = true,
            });

            // [段2] 轮询端口——起来了才算启动成功
            for (int i = 0; i < waitSeconds; i++)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                LocalComfyStatus probe = await ProbeAsync(ct).ConfigureAwait(false);
                if (probe.Running)
                {
                    probe.Message = "本地 ComfyUI 已启动";
                    return probe;
                }
            }

            LocalComfyStatus timeout = await ProbeAsync(ct).ConfigureAwait(false);
            if (!timeout.Running)
            {
                timeout.Message = "启动脚本已执行，但 " + waitSeconds + " 秒内端口未响应（可能仍在加载模型）";
            }

            return timeout;
        }
        finally
        {
            _launchGate.Release();
        }
    }

    /// <summary>用系统默认浏览器打开本地界面。</summary>
    public void OpenUi()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = _url,
            UseShellExecute = true,
        });
    }
}
