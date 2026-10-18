using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Rvc;

/// <summary>
/// 常驻 RVC 引擎的运行态——面板照它显示这一路的死活。
/// </summary>
public sealed class RvcEngineState
{
    /// <summary>状态：stopped / starting / ready / busy / error。</summary>
    public string State { get; set; } = "stopped";

    /// <summary>人话说明。</summary>
    public string Message { get; set; } = "";

    /// <summary>worker 的 torch 版本（就绪后才有）。</summary>
    public string Torch { get; set; } = "";

    /// <summary>worker 是否认到显卡（就绪后才有）。</summary>
    public bool Cuda { get; set; }

    /// <summary>实际推理设备名（就绪后才有）。</summary>
    public string Device { get; set; } = "";

    /// <summary>是否半精度（就绪后才有）。</summary>
    public bool Half { get; set; }

    /// <summary>worker 初始化耗时（秒，含 HuBERT 加载）。</summary>
    public double StartupSeconds { get; set; }

    /// <summary>本次启动时刻。</summary>
    public string StartedAt { get; set; } = "";
}

/// <summary>
/// RVC 常驻引擎——用 ComfyUI 的 python_embeded 跑一个 worker，
/// <para>HuBERT 特征提取器只加载一次，音色模型与检索索引按需切换并缓存（换模型才重载）。</para>
/// <para>孤儿防护：worker 从 stdin 读请求，父进程消失（含被强杀）即读到 EOF 自行退出，不留占显存的僵尸进程。</para>
/// </summary>
public sealed class RvcEngine : IDisposable
{
    /// <summary>状态锁。</summary>
    private readonly object _gate = new object();

    /// <summary>worker 的 stderr 尾巴（出错时给人看）。</summary>
    private readonly List<string> _stderr = new List<string>();

    /// <summary>worker 进程。</summary>
    private Process? _proc;

    /// <summary>解释器路径（ComfyUI 的 python_embeded\python.exe）。</summary>
    private string _python = "";

    /// <summary>rvc_lite 包的上级目录（worker 的 sys.path 落点）。</summary>
    private string _repo = "";

    /// <summary>模型根目录（ComfyUI\models\RVC）。</summary>
    private string _models = "";

    /// <summary>推理设备（cuda / cpu）。</summary>
    private string _device = "cuda";

    /// <summary>是否半精度。</summary>
    private bool _half = true;

    /// <summary>worker 脚本落点。</summary>
    private string _scriptPath = "";

    /// <summary>当前状态。</summary>
    private RvcEngineState _state = new RvcEngineState();

    /// <summary>是否已释放。</summary>
    private bool _disposed;

    /// <summary>当前状态（快照副本）。</summary>
    public RvcEngineState State
    {
        get
        {
            lock (_gate)
            {
                return new RvcEngineState
                {
                    State = _state.State,
                    Message = _state.Message,
                    Torch = _state.Torch,
                    Cuda = _state.Cuda,
                    Device = _state.Device,
                    Half = _state.Half,
                    StartupSeconds = _state.StartupSeconds,
                    StartedAt = _state.StartedAt,
                };
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

    /// <summary>
    /// 设定这一代 worker 的启动参数（只看磁盘，不启动进程）。
    /// <para>解释器 / 代码目录 / 模型根 / 设备 / 精度任一项变了，就把在跑的 worker 停掉——下次生成按新参数重载。</para>
    /// </summary>
    /// <param name="python">解释器。</param>
    /// <param name="repo">rvc_lite 上级目录。</param>
    /// <param name="models">模型根目录。</param>
    /// <param name="device">推理设备。</param>
    /// <param name="half">是否半精度。</param>
    /// <returns>解释器与代码目录都在位返回 true。</returns>
    public bool Prepare(string python, string repo, string models, string device, bool half)
    {
        bool changed = _python != python
            || _repo != repo
            || _models != models
            || _device != device
            || _half != half;

        if (changed && Alive)
        {
            Stop();
        }

        _python = python;
        _repo = repo;
        _models = models;
        _device = device.Length == 0 ? "cuda" : device;
        _half = half;

        string dataRoot = Path.Combine(AppContext.BaseDirectory, "data", "rvc");
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

        _scriptPath = Path.Combine(dataRoot, "rvc_worker.py");
        return File.Exists(_python) && Directory.Exists(_repo);
    }

    /// <summary>确保 worker 已就绪——没起就起，起了直接返回。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否就绪, 说明)。</returns>
    public async Task<(bool Ok, string Message)> EnsureStartedAsync(CancellationToken ct)
    {
        if (Alive)
        {
            return (true, "常驻引擎已在跑");
        }

        if (_scriptPath.Length == 0)
        {
            return (false, "worker 落点不可写（data\\rvc）");
        }

        if (!File.Exists(_python))
        {
            return (false, "找不到解释器：" + _python + "（本机 ComfyUI 的 python_embeded）");
        }

        if (!Directory.Exists(_repo))
        {
            return (false, "rvc_lite 代码目录不存在：" + _repo);
        }

        try
        {
            File.WriteAllText(_scriptPath, RvcWorkerScript.Source, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return (false, "写 worker 脚本失败：" + ex.Message);
        }

        SetState("starting", "正在加载 HuBERT 特征提取器（首次约 3~10 秒）…");

        string arguments = "-s \"" + _scriptPath + "\" --repo \"" + _repo + "\""
            + " --models \"" + _models + "\""
            + " --device \"" + _device + "\""
            + " --half \"" + (_half ? "1" : "0") + "\"";

        var psi = new ProcessStartInfo
        {
            FileName = _python,
            Arguments = arguments,
            WorkingDirectory = _repo,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        psi.EnvironmentVariables["PYTHONUTF8"] = "1";

        Process proc;
        try
        {
            proc = new Process { StartInfo = psi };
            proc.Start();
        }
        catch (Exception ex) when (ex is IOException || ex is Win32Exception || ex is InvalidOperationException)
        {
            SetState("error", "起引擎失败：" + ex.Message);
            return (false, "起引擎失败：" + ex.Message);
        }

        lock (_gate)
        {
            _stderr.Clear();
            _proc = proc;
        }

        proc.StandardInput.AutoFlush = true;
        _ = Task.Run(() => DrainStderr(proc));

        // HuBERT 360 M 从磁盘读进来，慢盘上给足 5 分钟
        string? line = await ReadLineWithTimeoutAsync(proc, 300000, ct).ConfigureAwait(false);
        if (line is null)
        {
            string tail = StderrTail();
            Stop();
            SetState("error", "引擎没在 5 分钟内就绪");
            return (false, "引擎没在 5 分钟内就绪" + (tail.Length > 0 ? "\n" + tail : ""));
        }

        JsonNode? node = TryParse(line);
        string ev = StringOf(node, "ev");
        if (ev == "error")
        {
            string message = StringOf(node, "message");
            string trace = StringOf(node, "traceback");
            Stop();
            SetState("error", "引擎启动失败：" + message);
            return (false, "引擎启动失败：" + message + (trace.Length > 0 ? "\n" + trace : ""));
        }

        if (ev != "ready")
        {
            Stop();
            SetState("error", "引擎回了意外事件：" + ev);
            return (false, "引擎回了意外事件：" + ev + "（" + line + "）");
        }

        double seconds = DoubleOf(node, "seconds");
        lock (_gate)
        {
            _state.State = "ready";
            _state.Torch = StringOf(node, "torch");
            _state.Cuda = BoolOf(node, "cuda");
            _state.Device = StringOf(node, "device");
            _state.Half = BoolOf(node, "half");
            _state.StartupSeconds = seconds;
            _state.StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _state.Message = "引擎就绪（" + seconds.ToString("F1") + " 秒 · "
                + (_state.Half ? "半精度" : "全精度") + "）";
        }

        return (true, _state.Message);
    }

    /// <summary>跑一次变声，产物直接落盘。</summary>
    /// <param name="input">输入音频完整路径。</param>
    /// <param name="model">音色模型文件名。</param>
    /// <param name="sid">说话人 id。</param>
    /// <param name="f0UpKey">变调半音数。</param>
    /// <param name="f0Method">F0 取法。</param>
    /// <param name="indexRate">检索混合比例。</param>
    /// <param name="filterRadius">中值滤波半径。</param>
    /// <param name="resampleSr">输出重采样率（0 = 模型自带）。</param>
    /// <param name="rmsMixRate">响度混合比例。</param>
    /// <param name="protect">清辅音保护。</param>
    /// <param name="outPath">输出 wav 完整路径。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 产物文件名, 音频秒数, 说明)。</returns>
    public async Task<(bool Ok, string File, double AudioSeconds, string Message)> RunAsync(
        string input,
        string model,
        int sid,
        double f0UpKey,
        string f0Method,
        double indexRate,
        int filterRadius,
        int resampleSr,
        double rmsMixRate,
        double protect,
        string outPath,
        CancellationToken ct)
    {
        (bool Ok, string Message) started = await EnsureStartedAsync(ct).ConfigureAwait(false);
        if (!started.Ok)
        {
            return (false, "", 0, started.Message);
        }

        Process? proc = _proc;
        if (proc is null)
        {
            return (false, "", 0, "引擎进程不见了");
        }

        lock (_gate)
        {
            if (_state.State == "busy")
            {
                return (false, "", 0, "上一段还在跑");
            }

            _state.State = "busy";
            _state.Message = "变声中…";
        }

        var request = new JsonObject
        {
            ["cmd"] = "run",
            ["id"] = Guid.NewGuid().ToString("N"),
            ["input"] = input,
            ["model"] = model,
            ["sid"] = sid,
            ["f0UpKey"] = f0UpKey,
            ["f0Method"] = f0Method,
            ["indexRate"] = indexRate,
            ["filterRadius"] = filterRadius,
            ["resampleSr"] = resampleSr,
            ["rmsMixRate"] = rmsMixRate,
            ["protect"] = protect,
            ["out"] = outPath,
        };

        try
        {
            await proc.StandardInput.WriteLineAsync(request.ToJsonString()).ConfigureAwait(false);
            await proc.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
        {
            SetState("error", "写不进引擎：" + ex.Message);
            return (false, "", 0, "写不进引擎：" + ex.Message);
        }

        // 一段最长等 30 分钟（长音频 + 慢卡留足余量）
        while (true)
        {
            string? line = await ReadLineWithTimeoutAsync(proc, 1800000, ct).ConfigureAwait(false);
            if (line is null)
            {
                string tail = StderrTail();
                SetState("error", "引擎没有回执");
                return (false, "", 0, "引擎没有回执" + (tail.Length > 0 ? "\n" + tail : ""));
            }

            JsonNode? node = TryParse(line);
            string ev = StringOf(node, "ev");
            if (ev == "pong" || ev == "start")
            {
                continue;
            }

            if (ev == "error")
            {
                string message = StringOf(node, "message");
                string stage = StringOf(node, "stage");
                string trace = StringOf(node, "traceback");
                SetState("ready", "上一段失败：" + message);
                return (false, "", 0, "引擎报错（" + stage + "）：" + message
                    + (trace.Length > 0 ? "\n" + trace : ""));
            }

            if (ev != "done")
            {
                continue;
            }

            string file = StringOf(node, "file");
            double audioSeconds = DoubleOf(node, "audioSeconds");
            double elapsed = DoubleOf(node, "seconds");
            SetState("ready", "引擎空闲（上一段 " + elapsed.ToString("F1") + " 秒）");
            if (file.Length == 0)
            {
                return (false, "", 0, "引擎跑完了，但没拿到音频文件");
            }

            return (true, file, audioSeconds, "出声 " + audioSeconds.ToString("F1") + " 秒（耗时 "
                + elapsed.ToString("F1") + " 秒）");
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

        SetState("stopped", "引擎已停止");
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

    /// <summary>把 worker 的 stderr 收进环形尾巴（库日志全在这里）。</summary>
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

    /// <summary>stderr 尾巴（最多 12 行）。</summary>
    /// <returns>文本。</returns>
    private string StderrTail()
    {
        lock (_gate)
        {
            if (_stderr.Count == 0)
            {
                return "";
            }

            int start = Math.Max(0, _stderr.Count - 12);
            var builder = new StringBuilder();
            for (int i = start; i < _stderr.Count; i++)
            {
                builder.AppendLine(_stderr[i]);
            }

            return builder.ToString().TrimEnd();
        }
    }

    /// <summary>改状态。</summary>
    /// <param name="state">新状态。</param>
    /// <param name="message">说明。</param>
    private void SetState(string state, string message)
    {
        lock (_gate)
        {
            _state.State = state;
            _state.Message = message;
        }
    }

    /// <summary>解析一行 JSON（失败返回 null）。</summary>
    /// <param name="line">原始行。</param>
    /// <returns>节点或 null。</returns>
    private static JsonNode? TryParse(string line)
    {
        try
        {
            return JsonNode.Parse(line);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>取字符串字段。</summary>
    /// <param name="node">节点。</param>
    /// <param name="name">字段名。</param>
    /// <returns>值（缺失给空串）。</returns>
    private static string StringOf(JsonNode? node, string name)
    {
        try
        {
            return node?[name]?.GetValue<string>() ?? "";
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is FormatException)
        {
            return "";
        }
    }

    /// <summary>取布尔字段。</summary>
    /// <param name="node">节点。</param>
    /// <param name="name">字段名。</param>
    /// <returns>值（缺失给 false）。</returns>
    private static bool BoolOf(JsonNode? node, string name)
    {
        try
        {
            return node?[name]?.GetValue<bool>() ?? false;
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is FormatException)
        {
            return false;
        }
    }

    /// <summary>取数字字段。</summary>
    /// <param name="node">节点。</param>
    /// <param name="name">字段名。</param>
    /// <returns>值（缺失给 0）。</returns>
    private static double DoubleOf(JsonNode? node, string name)
    {
        try
        {
            return node?[name]?.GetValue<double>() ?? 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is FormatException)
        {
            return 0;
        }
    }
}
