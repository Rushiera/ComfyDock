using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Tts;

/// <summary>
/// 常驻 TTS 引擎的运行态——面板照它显示 IrodoriTTS 这一路的死活。
/// </summary>
public sealed class TtsEngineState
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

    /// <summary>这一代 worker 加载的基座文件名（就绪后才有）。</summary>
    public string Base { get; set; } = "";

    /// <summary>这一代 worker 的模型精度（就绪后才有）。</summary>
    public string Precision { get; set; } = "";

    /// <summary>这一代 worker 的声码器设备（就绪后才有）。</summary>
    public string CodecDevice { get; set; } = "";

    /// <summary>worker 初始化耗时（秒，含基座与声码器加载）。</summary>
    public double StartupSeconds { get; set; }

    /// <summary>本次启动时刻。</summary>
    public string StartedAt { get; set; } = "";
}

/// <summary>
/// Irodori-TTS 常驻引擎——用 irodori-tts 自己的 venv 跑一个 worker，
/// <para>基座 + 声码器 + LoRA 只加载一次，之后每句只跑采样（首句 30~60 秒，之后十几秒）。</para>
/// <para>孤儿防护：worker 从 stdin 读请求，父进程消失（含被强杀）即读到 EOF 自行退出，不留占显存的僵尸进程。</para>
/// </summary>
public sealed class IrodoriTtsEngine : IDisposable
{
    /// <summary>状态锁。</summary>
    private readonly object _gate = new object();

    /// <summary>worker 的 stderr 尾巴（出错时给人看）。</summary>
    private readonly List<string> _stderr = new List<string>();

    /// <summary>worker 进程。</summary>
    private Process? _proc;

    /// <summary>解释器路径（venv 里的 python.exe）。</summary>
    private string _python = "";

    /// <summary>Irodori-TTS 仓库根目录（worker 的 sys.path 落点）。</summary>
    private string _repo = "";

    /// <summary>基座 safetensors 路径。</summary>
    private string _baseModel = "";

    /// <summary>声码器 weights.pth 路径。</summary>
    private string _codec = "";

    /// <summary>LoRA 适配器目录（空 = 不挂）。</summary>
    private string _lora = "";

    /// <summary>推理设备（cuda / cpu）。</summary>
    private string _device = "cuda";

    /// <summary>模型精度（bf16 / fp32）。</summary>
    private string _precision = "bf16";

    /// <summary>声码器设备（cuda / cpu）——大模型挤显存时把声码器挪到 CPU。</summary>
    private string _codecDevice = "cuda";

    /// <summary>worker 脚本落点。</summary>
    private string _scriptPath = "";

    /// <summary>当前状态。</summary>
    private TtsEngineState _state = new TtsEngineState();

    /// <summary>是否已释放。</summary>
    private bool _disposed;

    /// <summary>当前状态（快照副本）。</summary>
    public TtsEngineState State
    {
        get
        {
            lock (_gate)
            {
                return new TtsEngineState
                {
                    State = _state.State,
                    Message = _state.Message,
                    Torch = _state.Torch,
                    Cuda = _state.Cuda,
                    Device = _state.Device,
                    Base = _state.Base,
                    Precision = _state.Precision,
                    CodecDevice = _state.CodecDevice,
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
    /// <para>基座 / 声码器 / LoRA / 设备 / 精度任一项变了，就把在跑的 worker 停掉——下次生成按新参数重载。</para>
    /// </summary>
    /// <param name="python">venv 解释器。</param>
    /// <param name="repo">Irodori-TTS 仓库根目录。</param>
    /// <param name="baseModel">基座 safetensors。</param>
    /// <param name="codec">声码器 weights.pth。</param>
    /// <param name="lora">LoRA 适配器目录（可空）。</param>
    /// <param name="device">推理设备。</param>
    /// <param name="precision">模型精度。</param>
    /// <param name="codecDevice">声码器设备（空 = 跟随 device）。</param>
    /// <returns>解释器与仓库都在位返回 true。</returns>
    public bool Prepare(
        string python,
        string repo,
        string baseModel,
        string codec,
        string lora,
        string device,
        string precision,
        string codecDevice)
    {
        bool changed = _python != python
            || _repo != repo
            || _baseModel != baseModel
            || _codec != codec
            || _lora != lora
            || _device != device
            || _precision != precision
            || _codecDevice != codecDevice;

        if (changed && Alive)
        {
            Stop();
        }

        _python = python;
        _repo = repo;
        _baseModel = baseModel;
        _codec = codec;
        _lora = lora;
        _device = device.Length == 0 ? "cuda" : device;
        _precision = precision.Length == 0 ? "bf16" : precision;
        _codecDevice = codecDevice.Length == 0 ? _device : codecDevice;

        string dataRoot = Path.Combine(AppContext.BaseDirectory, "data", "tts");
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

        _scriptPath = Path.Combine(dataRoot, "tts_worker.py");
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
            return (false, "worker 落点不可写（data\\tts）");
        }

        if (!File.Exists(_python))
        {
            return (false, "找不到解释器：" + _python + "（先按说明建好 irodori-tts 的 venv）");
        }

        if (!Directory.Exists(_repo))
        {
            return (false, "Irodori-TTS 仓库目录不存在：" + _repo);
        }

        if (!File.Exists(_baseModel))
        {
            return (false, "基座模型不存在：" + _baseModel);
        }

        if (!File.Exists(_codec))
        {
            return (false, "声码器权重不存在：" + _codec);
        }

        try
        {
            File.WriteAllText(_scriptPath, TtsWorkerScript.Source, new UTF8Encoding(false));
        }
        catch (IOException ex)
        {
            return (false, "写 worker 脚本失败：" + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return (false, "写 worker 脚本失败：" + ex.Message);
        }

        SetState("starting", "正在加载基座与声码器（首次约 30~60 秒）…");

        string arguments = "-s \"" + _scriptPath + "\" --repo \"" + _repo + "\""
            + " --base-model \"" + _baseModel + "\""
            + " --codec \"" + _codec + "\""
            + " --device \"" + _device + "\""
            + " --codec-device \"" + _codecDevice + "\""
            + " --precision \"" + _precision + "\"";
        if (_lora.Length > 0)
        {
            arguments += " --lora \"" + _lora + "\"";
        }

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
        // 子进程三件套统一 UTF-8：否则 Windows 上 stdin 按本地代码页解码，日文变代理字符，tokenizers 拒收
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

        // 基座 2.85 G + 声码器 0.4 G 从磁盘读进来，慢盘上要给足 5 分钟
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

        string torch = StringOf(node, "torch");
        bool cuda = BoolOf(node, "cuda");
        string device = StringOf(node, "device");
        string baseName = StringOf(node, "base");
        string precision = StringOf(node, "precision");
        string codecDevice = StringOf(node, "codecDevice");
        double seconds = DoubleOf(node, "seconds");

        lock (_gate)
        {
            _state.State = "ready";
            _state.Torch = torch;
            _state.Cuda = cuda;
            _state.Device = device;
            _state.Base = baseName;
            _state.Precision = precision;
            _state.CodecDevice = codecDevice;
            _state.StartupSeconds = seconds;
            _state.StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _state.Message = "引擎就绪（" + seconds.ToString("F1") + " 秒 · " + baseName
                + " · " + precision + " · 声码器 " + codecDevice + "）";
        }

        return (true, _state.Message);
    }

    /// <summary>跑一句，产物直接落盘。</summary>
    /// <param name="text">要合成的文本。</param>
    /// <param name="caption">风格描述（可空）。</param>
    /// <param name="refs">参考音频完整路径（可空）。</param>
    /// <param name="noRef">不用参考音频。</param>
    /// <param name="steps">采样步数（0 = 用运行时默认 40）。</param>
    /// <param name="seed">种子（-1 = 随机）。</param>
    /// <param name="durationScale">时长倍率（0 = 默认 1）。</param>
    /// <param name="seconds">强制时长（0 = 自动预测）。</param>
    /// <param name="useLora">是否挂 LoRA。</param>
    /// <param name="outPath">输出 wav 完整路径。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 产物文件名, 音频秒数, 实际种子, 说明)。</returns>
    public async Task<(bool Ok, string File, double AudioSeconds, long Seed, string Message)> RunAsync(
        string text,
        string caption,
        List<string> refs,
        bool noRef,
        int steps,
        long seed,
        double durationScale,
        double seconds,
        bool useLora,
        string outPath,
        CancellationToken ct)
    {
        (bool Ok, string Message) started = await EnsureStartedAsync(ct).ConfigureAwait(false);
        if (!started.Ok)
        {
            return (false, "", 0, 0, started.Message);
        }

        Process? proc = _proc;
        if (proc is null)
        {
            return (false, "", 0, 0, "引擎进程不见了");
        }

        lock (_gate)
        {
            if (_state.State == "busy")
            {
                return (false, "", 0, 0, "上一句还在跑");
            }

            _state.State = "busy";
            _state.Message = "合成中…";
        }

        string id = Guid.NewGuid().ToString("N");
        var request = new JsonObject
        {
            ["cmd"] = "run",
            ["id"] = id,
            ["text"] = text,
            ["caption"] = caption,
            ["noRef"] = noRef,
            ["steps"] = steps,
            ["seed"] = seed,
            ["durationScale"] = durationScale,
            ["seconds"] = seconds,
            ["useLora"] = useLora,
            ["out"] = outPath,
        };

        var refArray = new JsonArray();
        foreach (string item in refs)
        {
            refArray.Add(item);
        }

        request["refs"] = refArray;

        try
        {
            await proc.StandardInput.WriteLineAsync(request.ToJsonString()).ConfigureAwait(false);
            await proc.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
        {
            SetState("error", "写不进引擎：" + ex.Message);
            return (false, "", 0, 0, "写不进引擎：" + ex.Message);
        }

        // 每读一行最长等 10 分钟——分段合成时每段结束都有一行回执，超长文本不会撞这个闸
        while (true)
        {
            string? line = await ReadLineWithTimeoutAsync(proc, 600000, ct).ConfigureAwait(false);
            if (line is null)
            {
                string tail = StderrTail();
                SetState("error", "引擎没有回执");
                return (false, "", 0, 0, "引擎没有回执" + (tail.Length > 0 ? "\n" + tail : ""));
            }

            JsonNode? node = TryParse(line);
            string ev = StringOf(node, "ev");
            if (ev == "pong" || ev == "start")
            {
                continue;
            }

            if (ev == "plan")
            {
                int total = (int)DoubleOf(node, "chunks");
                SetState("busy", "按句切段合成中（共 " + total + " 段）…");
                continue;
            }

            if (ev == "chunk")
            {
                int index = (int)DoubleOf(node, "index");
                int total = (int)DoubleOf(node, "total");
                SetState("busy", "按句切段合成中 " + index + "/" + total + "…");
                continue;
            }

            if (ev == "error")
            {
                string message = StringOf(node, "message");
                string stage = StringOf(node, "stage");
                string trace = StringOf(node, "traceback");
                SetState("ready", "上一句失败：" + message);
                return (false, "", 0, 0, "引擎报错（" + stage + "）：" + message
                    + (trace.Length > 0 ? "\n" + trace : ""));
            }

            if (ev != "done")
            {
                continue;
            }

            string file = StringOf(node, "file");
            double audioSeconds = DoubleOf(node, "audioSeconds");
            double elapsed = DoubleOf(node, "seconds");
            long usedSeed = (long)DoubleOf(node, "seed");
            int chunks = (int)DoubleOf(node, "chunks");
            string warnings = JoinWarnings(node);
            SetState("ready", "引擎空闲（上一句 " + elapsed.ToString("F1") + " 秒）");
            if (file.Length == 0)
            {
                return (false, "", 0, 0, "引擎跑完了，但没拿到音频文件");
            }

            string note = "出声 " + audioSeconds.ToString("F1") + " 秒（耗时 " + elapsed.ToString("F1") + " 秒"
                + (chunks > 1 ? "，按句切 " + chunks + " 段" : "") + "）"
                + (warnings.Length > 0 ? "；" + warnings : "");
            return (true, file, audioSeconds, usedSeed, note);
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

    /// <summary>把 worker 的 stderr 收进环形尾巴（模型 / 水印日志全在这里）。</summary>
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
    public string StderrTail()
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
    /// <summary>把 done 回执里的 warnings 数组拼成给人看的一行（每行一条）。</summary>
    /// <param name="node">done 回执节点。</param>
    /// <returns>换行分隔的告警；没有则空串。</returns>
    private static string JoinWarnings(JsonNode? node)
    {
        if (node is null || node["warnings"] is not JsonArray array || array.Count == 0)
        {
            return "";
        }

        var lines = new List<string>();
        foreach (JsonNode? item in array)
        {
            string text = (item?.GetValue<string>() ?? "").Trim();
            if (text.Length > 0)
            {
                lines.Add(text);
            }
        }

        return string.Join("；", lines);
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
