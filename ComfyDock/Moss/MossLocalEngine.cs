using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Moss;

/// <summary>
/// 内置推理引擎的运行态——面板照它显示「内置引擎」这一路的死活。
/// </summary>
public sealed class MossLocalState
{
    /// <summary>状态：stopped / starting / ready / busy / error。</summary>
    public string State { get; set; } = "stopped";

    /// <summary>人话说明。</summary>
    public string Message { get; set; } = "";

    /// <summary>worker 用的 torch 版本（就绪后才有）。</summary>
    public string Torch { get; set; } = "";

    /// <summary>worker 是否认到显卡（就绪后才有）。</summary>
    public bool Cuda { get; set; }

    /// <summary>worker 初始化耗时（秒）。</summary>
    public double StartupSeconds { get; set; }

    /// <summary>本次启动时刻。</summary>
    public string StartedAt { get; set; } = "";
}

/// <summary>
/// 内置推理引擎——用 ComfyUI 的便携解释器跑一个常驻 worker，
/// <para>只借它的推理内核（comfy 包 + custom_nodes 里的 MOSS 插件），<b>不启动 ComfyUI 的 HTTP 服务</b>：
/// 没有 8188 端口、没有 ComfyUI 界面，所以「ComfyUI 没开」也能出图。模型仍然读 ComfyUI 的 models 目录。</para>
/// <para>孤儿防护：worker 从 stdin 读请求，父进程消失（含被强杀）即读到 EOF 自行退出，不留占显存的僵尸进程。</para>
/// </summary>
public sealed class MossLocalEngine : IDisposable
{
    /// <summary>状态锁。</summary>
    private readonly object _gate = new object();

    /// <summary>worker 的 stderr 尾巴（出错时给人看）。</summary>
    private readonly List<string> _stderr = new List<string>();

    /// <summary>worker 进程。</summary>
    private Process? _proc;

    /// <summary>ComfyUI 根目录。</summary>
    private string _comfyRoot = "";

    /// <summary>便携解释器路径。</summary>
    private string _python = "";

    /// <summary>worker 脚本落点。</summary>
    private string _scriptPath = "";

    /// <summary>当前状态。</summary>
    private MossLocalState _state = new MossLocalState();

    /// <summary>是否已释放。</summary>
    private bool _disposed;

    /// <summary>当前状态（快照副本）。</summary>
    public MossLocalState State
    {
        get
        {
            lock (_gate)
            {
                var copy = new MossLocalState
                {
                    State = _state.State,
                    Message = _state.Message,
                    Torch = _state.Torch,
                    Cuda = _state.Cuda,
                    StartupSeconds = _state.StartupSeconds,
                    StartedAt = _state.StartedAt,
                };
                return copy;
            }
        }
    }

    /// <summary>解释器路径（面板显示用）。</summary>
    public string PythonPath
    {
        get { return _python; }
    }

    /// <summary>worker 脚本落点。</summary>
    public string ScriptPath
    {
        get { return _scriptPath; }
    }

    /// <summary>worker 进程是否还活着。</summary>
    public bool Alive
    {
        get
        {
            Process? proc = _proc;
            if (proc is null)
            {
                return false;
            }

            try
            {
                return !proc.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>解释器与脚本落点是否就绪（只看磁盘，不启动进程）。</summary>
    /// <param name="comfyRoot">ComfyUI 根目录。</param>
    /// <returns>解释器在位返回 true。</returns>
    public bool Prepare(string comfyRoot)
    {
        _comfyRoot = comfyRoot;
        string installRoot = Path.GetDirectoryName(comfyRoot.TrimEnd('\\', '/')) ?? comfyRoot;
        _python = Path.Combine(installRoot, "python_embeded", "python.exe");

        string dataRoot = Path.Combine(AppContext.BaseDirectory, "data", "moss");
        try
        {
            Directory.CreateDirectory(dataRoot);
        }
        catch (IOException)
        {
            _scriptPath = "";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            _scriptPath = "";
            return false;
        }

        _scriptPath = Path.Combine(dataRoot, "moss_worker.py");
        return File.Exists(_python);
    }

    /// <summary>确保 worker 已就绪——没起就起，起了直接返回。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否就绪, 说明)。</returns>
    public async Task<(bool Ok, string Message)> EnsureStartedAsync(CancellationToken ct)
    {
        if (Alive)
        {
            return (true, "内置引擎已在跑");
        }

        if (_scriptPath.Length == 0)
        {
            return (false, "内置引擎落点不可写（data\\moss）");
        }

        if (!File.Exists(_python))
        {
            return (false, "找不到引擎解释器：" + _python + "（本机不是便携整合包？改用手工启动 ComfyUI）");
        }

        if (!File.Exists(Path.Combine(_comfyRoot, "main.py")))
        {
            return (false, "ComfyUI 目录不完整（缺 main.py）：" + _comfyRoot);
        }

        try
        {
            File.WriteAllText(_scriptPath, MossWorkerScript.Source, new UTF8Encoding(false));
        }
        catch (IOException ex)
        {
            return (false, "写 worker 脚本失败：" + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return (false, "写 worker 脚本失败：" + ex.Message);
        }

        SetState("starting", "正在起内置引擎（首次加载节点表约 30 秒，模型首次加载另算）…");

        var psi = new ProcessStartInfo
        {
            FileName = _python,
            Arguments = "-s \"" + _scriptPath + "\" \"" + _comfyRoot + "\"",
            WorkingDirectory = _comfyRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        Process proc;
        try
        {
            proc = new Process { StartInfo = psi };
            proc.Start();
        }
        catch (Exception ex) when (ex is IOException || ex is Win32Exception || ex is InvalidOperationException)
        {
            SetState("error", "起内置引擎失败：" + ex.Message);
            return (false, "起内置引擎失败：" + ex.Message);
        }

        lock (_gate)
        {
            _stderr.Clear();
            _proc = proc;
        }

        proc.StandardInput.AutoFlush = true;
        _ = Task.Run(() => DrainStderr(proc));

        // 首次要加载 24 个自定义节点包——给足 4 分钟
        string? line = await ReadLineWithTimeoutAsync(proc, 240000, ct).ConfigureAwait(false);
        if (line is null)
        {
            string tail = StderrTail();
            Stop();
            SetState("error", "内置引擎没在 4 分钟内就绪");
            return (false, "内置引擎没在 4 分钟内就绪" + (tail.Length > 0 ? "\n" + tail : ""));
        }

        JsonNode? node = TryParse(line);
        string ev = StringOf(node, "ev");
        if (ev == "error")
        {
            string message = StringOf(node, "message");
            string trace = StringOf(node, "traceback");
            Stop();
            SetState("error", "内置引擎启动失败：" + message);
            return (false, "内置引擎启动失败：" + message + (trace.Length > 0 ? "\n" + trace : ""));
        }

        if (ev != "ready")
        {
            Stop();
            SetState("error", "内置引擎回了意外事件：" + ev);
            return (false, "内置引擎回了意外事件：" + ev + "（" + line + "）");
        }

        string torch = StringOf(node, "torch");
        bool cuda = BoolOf(node, "cuda");
        double seconds = DoubleOf(node, "seconds");
        bool moss = BoolOf(node, "moss");

        lock (_gate)
        {
            _state.State = "ready";
            _state.Torch = torch;
            _state.Cuda = cuda;
            _state.StartupSeconds = seconds;
            _state.StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _state.Message = moss
                ? "内置引擎就绪（" + seconds.ToString("F1") + " 秒 · torch " + torch + "）"
                : "内置引擎就绪，但 MOSS 插件没被加载——先点「安装插件」";
        }

        if (!moss)
        {
            return (false, "内置引擎已起，但内核没加载到 MOSS 插件（custom_nodes\\moss-image 缺失或被内核拒绝）");
        }

        return (true, "内置引擎就绪（" + seconds.ToString("F1") + " 秒）");
    }

    /// <summary>跑一次节点图，产物直接落到本地目录。</summary>
    /// <param name="graph">API 格式节点图。</param>
    /// <param name="outDir">产物目录。</param>
    /// <param name="seed">种子（产物命名前缀）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 产物文件名, 说明)。</returns>
    public async Task<(bool Ok, List<string> Files, string Message)> RunAsync(
        JsonObject graph,
        string outDir,
        long seed,
        CancellationToken ct)
    {
        (bool Ok, string Message) started = await EnsureStartedAsync(ct).ConfigureAwait(false);
        if (!started.Ok)
        {
            return (false, new List<string>(), started.Message);
        }

        Process? proc = _proc;
        if (proc is null)
        {
            return (false, new List<string>(), "内置引擎进程不见了");
        }

        lock (_gate)
        {
            if (_state.State == "busy")
            {
                return (false, new List<string>(), "内置引擎上一张还在跑");
            }

            _state.State = "busy";
            _state.Message = "内置引擎跑图中…";
        }

        string id = Guid.NewGuid().ToString("N");
        var request = new JsonObject
        {
            ["cmd"] = "run",
            ["id"] = id,
            ["prompt"] = graph.DeepClone(),
            ["outDir"] = outDir,
            ["seed"] = seed,
        };

        try
        {
            await proc.StandardInput.WriteLineAsync(request.ToJsonString()).ConfigureAwait(false);
            await proc.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
        {
            SetState("error", "写不进内置引擎：" + ex.Message);
            return (false, new List<string>(), "写不进内置引擎：" + ex.Message);
        }

        // 生成最长等 3 小时（与 HTTP 通道同口径）
        while (true)
        {
            string? line = await ReadLineWithTimeoutAsync(proc, 10800000, ct).ConfigureAwait(false);
            if (line is null)
            {
                string tail = StderrTail();
                SetState("error", "内置引擎没有回执");
                return (false, new List<string>(), "内置引擎没有回执" + (tail.Length > 0 ? "\n" + tail : ""));
            }

            JsonNode? node = TryParse(line);
            string ev = StringOf(node, "ev");
            if (ev == "pong")
            {
                continue;
            }

            if (ev == "error")
            {
                string message = StringOf(node, "message");
                string stage = StringOf(node, "stage");
                string trace = StringOf(node, "traceback");
                SetState("ready", "上一次失败：" + message);
                return (false, new List<string>(), "内置引擎报错（" + stage + "）：" + message
                    + (trace.Length > 0 ? "\n" + trace : ""));
            }

            if (ev != "done")
            {
                continue;
            }

            var files = new List<string>();
            JsonArray? array = node is null ? null : node["files"] as JsonArray;
            if (array is not null)
            {
                foreach (JsonNode? item in array)
                {
                    if (item is null)
                    {
                        continue;
                    }

                    files.Add(item.GetValue<string>());
                }
            }

            double seconds = DoubleOf(node, "seconds");
            SetState("ready", "内置引擎空闲（上一张 " + seconds.ToString("F1") + " 秒）");
            if (files.Count == 0)
            {
                return (false, files, "内置引擎跑完了，但没拿到图片文件");
            }

            return (true, files, "出图 " + files.Count + " 张（" + seconds.ToString("F1") + " 秒）");
        }
    }

    /// <summary>停掉 worker（发 quit，4 秒不退就强杀）。</summary>
    public void Stop()
    {
        Process? proc;
        lock (_gate)
        {
            proc = _proc;
            _proc = null;
        }

        if (proc is not null)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.StandardInput.WriteLine("{\"cmd\":\"quit\"}");
                    proc.StandardInput.Flush();
                    if (!proc.WaitForExit(4000))
                    {
                        proc.Kill(true);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is Win32Exception)
            {
                lock (_gate)
                {
                    _stderr.Add("停止时：" + ex.Message);
                }
            }

            proc.Dispose();
        }

        SetState("stopped", "内置引擎已停止");
    }

    /// <summary>释放。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    /// <summary>读一行协议输出，超时返回 null。</summary>
    /// <param name="proc">worker 进程。</param>
    /// <param name="ms">超时毫秒。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>一行文本；超时 / 流结束返回 null。</returns>
    private static async Task<string?> ReadLineWithTimeoutAsync(Process proc, int ms, CancellationToken ct)
    {
        Task<string?> read;
        try
        {
            read = proc.StandardOutput.ReadLineAsync(ct).AsTask();
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        Task finished = await Task.WhenAny(read, Task.Delay(ms, ct)).ConfigureAwait(false);
        if (finished != read)
        {
            return null;
        }

        try
        {
            return await read.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException || ex is OperationCanceledException || ex is ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>把 worker 的 stderr 收进环形尾巴（自定义节点日志全在这里）。</summary>
    /// <param name="proc">worker 进程。</param>
    /// <returns>无。</returns>
    private async Task DrainStderr(Process proc)
    {
        try
        {
            while (true)
            {
                string? line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                lock (_gate)
                {
                    _stderr.Add(line);
                    if (_stderr.Count > 60)
                    {
                        _stderr.RemoveAt(0);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
        {
            return;
        }
    }

    /// <summary>stderr 尾巴（最后 20 行）。</summary>
    /// <returns>文本；空尾巴返回空串。</returns>
    private string StderrTail()
    {
        lock (_gate)
        {
            if (_stderr.Count == 0)
            {
                return "";
            }

            int start = _stderr.Count > 20 ? _stderr.Count - 20 : 0;
            var builder = new StringBuilder();
            for (int i = start; i < _stderr.Count; i++)
            {
                builder.AppendLine(_stderr[i]);
            }

            return builder.ToString().TrimEnd();
        }
    }

    /// <summary>更新状态。</summary>
    /// <param name="state">状态名。</param>
    /// <param name="message">说明。</param>
    /// <returns>无。</returns>
    private void SetState(string state, string message)
    {
        lock (_gate)
        {
            _state.State = state;
            _state.Message = message;
        }
    }

    /// <summary>解析一行 JSON（解析失败返回 null）。</summary>
    /// <param name="line">一行文本。</param>
    /// <returns>节点；失败返回 null。</returns>
    private static JsonNode? TryParse(string line)
    {
        try
        {
            return JsonNode.Parse(line);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException || ex is ArgumentException)
        {
            return null;
        }
    }

    /// <summary>取字符串字段。</summary>
    /// <param name="node">节点。</param>
    /// <param name="name">字段名。</param>
    /// <returns>值；缺失返回空串。</returns>
    private static string StringOf(JsonNode? node, string name)
    {
        if (node is null)
        {
            return "";
        }

        JsonNode? value = node[name];
        if (value is null)
        {
            return "";
        }

        try
        {
            return value.GetValue<string>();
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is FormatException)
        {
            return "";
        }
    }

    /// <summary>取布尔字段。</summary>
    /// <param name="node">节点。</param>
    /// <param name="name">字段名。</param>
    /// <returns>值；缺失返回 false。</returns>
    private static bool BoolOf(JsonNode? node, string name)
    {
        if (node is null)
        {
            return false;
        }

        JsonNode? value = node[name];
        if (value is null)
        {
            return false;
        }

        try
        {
            return value.GetValue<bool>();
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is FormatException)
        {
            return false;
        }
    }

    /// <summary>取数值字段。</summary>
    /// <param name="node">节点。</param>
    /// <param name="name">字段名。</param>
    /// <returns>值；缺失返回 0。</returns>
    private static double DoubleOf(JsonNode? node, string name)
    {
        if (node is null)
        {
            return 0;
        }

        JsonNode? value = node[name];
        if (value is null)
        {
            return 0;
        }

        try
        {
            return value.GetValue<double>();
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is FormatException)
        {
            return 0;
        }
    }
}
