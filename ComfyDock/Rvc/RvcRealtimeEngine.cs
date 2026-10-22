using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Rvc;

/// <summary>
/// RVC 实时变声的 C# 侧引擎——起一个流式 worker，双向跑行协议（请求 cmd / 事件 ev）。
/// <para>与离线引擎分立的第二套进程：离线是「一请求一响应」，实时是「流式状态机」——开流后 worker 持续收发音频块，C# 侧只做控制与事件泵。</para>
/// <para>协议以 <c>RvcRealtimeWorkerScript</c>（线 2 产出）为准：请求 <c>devices</c> / <c>start</c> / <c>stop</c> / <c>update</c> / <c>ping</c> / <c>quit</c>；
/// 事件 <c>ready</c>（进程就绪）/ <c>started</c>（开流成功）/ <c>stopped</c> / <c>updated</c> / <c>level</c> / <c>latency</c> / <c>error</c>。</para>
/// <para>孤儿防护：worker 从 stdin 读请求，父进程消失即读到 EOF 自行退出，不留占麦克风的僵尸进程。</para>
/// </summary>
public sealed class RvcRealtimeEngine : IDisposable
{
    /// <summary>状态锁（状态 / stderr / 等待者共用）。</summary>
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

    /// <summary>worker 脚本落点（data\rvc\rvc_realtime_worker.py）。</summary>
    private string _scriptPath = "";

    /// <summary>当前状态。</summary>
    private RvcRealtimeState _state = new RvcRealtimeState();

    /// <summary>当前等待的回执（同一时刻只有一个请求在等）。</summary>
    private TaskCompletionSource<JsonNode?>? _waiter;

    /// <summary>等待的事件名（空 = 没在等）。</summary>
    private string _waitEv = "";

    /// <summary>是否已释放。</summary>
    private bool _disposed;

    /// <summary>当前状态（快照副本）。</summary>
    public RvcRealtimeState State
    {
        get
        {
            lock (_gate)
            {
                return new RvcRealtimeState
                {
                    State = _state.State,
                    Message = _state.Message,
                    SampleRate = _state.SampleRate,
                    BlockMs = _state.BlockMs,
                    CrossfadeMs = _state.CrossfadeMs,
                    ExtraMs = _state.ExtraMs,
                    DeviceMs = _state.DeviceMs,
                    LatencyMs = _state.LatencyMs,
                    InferMs = _state.InferMs,
                    LevelInDb = _state.LevelInDb,
                    LevelOutDb = _state.LevelOutDb,
                    Worker = _state.Worker,
                    Bypass = _state.Bypass,
                    BridgeEnabled = _state.BridgeEnabled,
                    BridgeBlocks = _state.BridgeBlocks,
                    BridgeState = _state.BridgeState,
                    BridgeMessage = _state.BridgeMessage,
                    BridgeModel = _state.BridgeModel,
                    BridgeSrc = _state.BridgeSrc,
                    BridgeDst = _state.BridgeDst,
                    BridgeMs = _state.BridgeMs,
                    BridgeInferMs = _state.BridgeInferMs,
                    BridgeUnderruns = _state.BridgeUnderruns,
                    Model = _state.Model,
                    StartedAt = _state.StartedAt,
                    StderrTail = new List<string>(_state.StderrTail),
                };
            }
        }
    }

    /// <summary>worker 脚本落点（面板显示用）。</summary>
    public string ScriptPath
    {
        get { return _scriptPath; }
    }
    /// <summary>桥接用的 VITS 设置（服务层按 VITS 页签当前配置填；null / 空模型 = 桥接不可用）。</summary>
    public RvcVitsBridgeSpec? BridgeSpec { get; set; }

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
    /// <para>解释器 / 代码目录 / 模型根 / 设备 / 精度任一项变了，就把在跑的 worker 停掉——下次开流按新参数重载。</para>
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
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _scriptPath = "";
            return false;
        }

        _scriptPath = Path.Combine(dataRoot, "rvc_realtime_worker.py");
        return File.Exists(_python) && Directory.Exists(_repo);
    }

    /// <summary>
    /// 起 worker 并按配置开流（已开流时先停再开）。
    /// </summary>
    /// <param name="config">实时配置。</param>
    /// <param name="workerSource">worker 脚本源码（空 = 脚本已在盘上，直接跑）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 人话)。</returns>
    public async Task<(bool Ok, string Message)> StartAsync(
        RvcRealtimeConfig config,
        string workerSource,
        CancellationToken ct)
    {
        if (!EnsureWorkerReady(workerSource, out string problem))
        {
            SetState("error", problem);
            return (false, problem);
        }

        SetState("starting", "正在起流式 worker（首次要加载 HuBERT，约 3~15 秒）…");

        JsonObject request = BuildStartRequest(config, BridgeSpec);
        (bool ok, JsonNode? node, string message) = await RequestAsync(
            request,
            RvcRealtimeProtocol.EvStarted,
            300000,
            ct).ConfigureAwait(false);

        if (!ok)
        {
            SetState("error", message);
            return (false, message);
        }

        int sr = config.SampleRate;
        if (node != null)
        {
            JsonNode? info = node["info"];
            double actual = DoubleOf(info, "samplerate");
            if (actual > 0)
            {
                sr = (int)actual;
            }

            ApplyLatency(node["latency"], 0);
        }

        int actualSid = node != null ? (int)DoubleOf(node, "sid") : config.Sid;
        int speakerCount = node != null ? (int)DoubleOf(node, "nSpk") : 0;
        string sidNote = speakerCount > 0 && actualSid != config.Sid
            ? "；该音色只有 " + speakerCount + " 个说话人，说话人已归 " + actualSid
            : "";

        lock (_gate)
        {
            _state.State = "running";
            _state.SampleRate = sr;
            _state.Bypass = config.Bypass;
            _state.Model = Path.GetFileName(config.Model);
            _state.StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _state.Message = "实时变声已开流（" + sr + " Hz"
                + (config.Bypass ? " · 干声直通" : " · 变声")
                + "）" + sidNote;
        }

        return (true, State.Message);
    }

    /// <summary>
    /// 热更参数（只带白名单字段；其余字段提示走重开）。
    /// </summary>
    /// <param name="config">新配置。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 人话, 生效字段)。</returns>
    public async Task<(bool Ok, string Message, List<string> Applied)> UpdateAsync(
        RvcRealtimeConfig config,
        CancellationToken ct)
    {
        if (!Alive)
        {
            return (false, "实时 worker 没在跑——先开流", new List<string>());
        }

        var parameters = new JsonObject
        {
            ["f0UpKey"] = config.F0UpKey,
            ["formant"] = config.Formant,
            ["indexRate"] = config.IndexRate,
            ["threhold"] = config.Threshold,
            ["inputNoiseReduce"] = config.InputDenoise,
            ["outputNoiseReduce"] = config.OutputDenoise,
            ["bypass"] = config.Bypass,
        };

        var request = new JsonObject
        {
            ["cmd"] = RvcRealtimeProtocol.CmdUpdate,
            ["params"] = parameters,
        };

        JsonObject? bridgeNode = BuildBridgeNode(config, BridgeSpec);
        if (bridgeNode != null)
        {
            request["bridge"] = bridgeNode;
        }

        (bool ok, JsonNode? node, string message) = await RequestAsync(
            request,
            RvcRealtimeProtocol.EvUpdated,
            15000,
            ct).ConfigureAwait(false);

        if (!ok)
        {
            return (false, message, new List<string>());
        }

        var applied = new List<string>();
        JsonObject? map = node?["applied"] as JsonObject;
        if (map != null)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in map)
            {
                applied.Add(pair.Key);
            }
        }

        ApplyBridge(node?["bridge"]);

        lock (_gate)
        {
            _state.Bypass = config.Bypass;
            _state.Message = config.Bypass ? "干声直通（旁路推理）" : "变声中";
        }

        return (true, "热更已生效" + (applied.Count > 0 ? "：" + string.Join(" / ", applied) : ""), applied);
    }

    /// <summary>停流（进程保留，下次开流不用重载模型）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 人话)。</returns>
    public async Task<(bool Ok, string Message)> StopStreamAsync(CancellationToken ct)
    {
        if (!Alive)
        {
            SetState("stopped", "实时 worker 没在跑");
            return (true, "实时 worker 没在跑");
        }

        var request = new JsonObject
        {
            ["cmd"] = RvcRealtimeProtocol.CmdStop,
        };

        (bool ok, JsonNode? node, string message) = await RequestAsync(
            request,
            RvcRealtimeProtocol.EvStopped,
            15000,
            ct).ConfigureAwait(false);

        SetState("stopped", ok ? "实时变声已停流（worker 保留，模型不重载）" : message);
        return (ok, ok ? "实时变声已停流" : message);
    }

    /// <summary>
    /// 枚举音频设备（hostapi → 输入 / 输出清单）。
    /// <para>不加载模型、不开流——面板选设备时随时可调。</para>
    /// </summary>
    /// <param name="workerSource">worker 脚本源码（空 = 脚本已在盘上）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 设备清单, 人话)。</returns>
    public async Task<(bool Ok, List<RvcHostApiItem> HostApis, string Message)> DevicesAsync(
        string workerSource,
        CancellationToken ct)
    {
        var empty = new List<RvcHostApiItem>();
        if (!EnsureWorkerReady(workerSource, out string problem))
        {
            return (false, empty, problem);
        }

        var request = new JsonObject
        {
            ["cmd"] = RvcRealtimeProtocol.CmdDevices,
        };

        (bool ok, JsonNode? node, string message) = await RequestAsync(
            request,
            RvcRealtimeProtocol.EvDevices,
            120000,
            ct).ConfigureAwait(false);

        if (!ok)
        {
            return (false, empty, message);
        }

        List<RvcHostApiItem> hostApis = ParseDevices(node);
        return (true, hostApis, "设备清单 " + hostApis.Count + " 组");
    }

    /// <summary>
    /// 自检——用独立进程跑一段正弦进同一条推理链（不开设备、不动麦克风），回延迟与输出时长。
    /// <para>走 worker 的 <c>--selftest</c> 通道；产物 wav 落 <c>data\rvc\rt_selftest.wav</c>（自检产物，不是录音）。</para>
    /// </summary>
    /// <param name="seconds">正弦秒数。</param>
    /// <param name="outIndex">输出设备索引（自检不开设备，仅记录在说明里）。</param>
    /// <param name="sampleRate">采样率。</param>
    /// <param name="config">参数（音色 / F0 / 检索混合等——selftest 走同一条链）。</param>
    /// <param name="workerSource">worker 脚本源码（空 = 脚本已在盘上）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 状态快照, 人话)。</returns>
    public async Task<(bool Ok, RvcRealtimeState Result, string Message)> SelftestAsync(
        int seconds,
        int outIndex,
        int sampleRate,
        RvcRealtimeConfig config,
        string workerSource,
        CancellationToken ct)
    {
        if (!EnsureScriptOnDisk(workerSource, out string problem, out _))
        {
            return (false, State, problem);
        }

        string outPath = Path.Combine(AppContext.BaseDirectory, "data", "rvc", "rt_selftest.wav");
        var arguments = new StringBuilder();
        arguments.Append("-s \"").Append(_scriptPath).Append("\"");
        arguments.Append(" --repo \"").Append(_repo).Append("\"");
        arguments.Append(" --models \"").Append(_models).Append("\"");
        arguments.Append(" --device \"").Append(_device).Append("\"");
        arguments.Append(" --half \"").Append(_half ? "1" : "0").Append("\"");
        arguments.Append(" --selftest");
        arguments.Append(" --model \"").Append(config.Model).Append("\"");
        arguments.Append(" --samplerate ").Append(sampleRate);
        arguments.Append(" --seconds ").Append(seconds);
        arguments.Append(" --f0 ").Append(config.F0Method);
        arguments.Append(" --key ").Append(config.F0UpKey.ToString("0.###"));
        arguments.Append(" --index ").Append(config.IndexRate.ToString("0.###"));
        arguments.Append(" --block ").Append(config.BlockTime.ToString("0.###"));
        arguments.Append(" --crossfade ").Append(config.Crossfade.ToString("0.###"));
        arguments.Append(" --extra ").Append(config.ExtraTime.ToString("0.###"));
        arguments.Append(" --out \"").Append(outPath).Append("\"");
        if (config.Bypass)
        {
            arguments.Append(" --bypass");
        }

        if (config.InputDenoise)
        {
            arguments.Append(" --nr-in");
        }

        if (config.OutputDenoise)
        {
            arguments.Append(" --nr-out");
        }

        var psi = new ProcessStartInfo
        {
            FileName = _python,
            Arguments = arguments.ToString(),
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
            return (false, State, "起自检进程失败：" + ex.Message);
        }

        lock (_gate)
        {
            _stderr.Clear();
        }

        _ = Task.Run(() => DrainStderr(proc));

        JsonNode? result = null;
        string failure = "";
        try
        {
            while (true)
            {
                Task<string?> read = proc.StandardOutput.ReadLineAsync();
                Task finished = await Task.WhenAny(read, Task.Delay(180000, ct)).ConfigureAwait(false);
                if (finished != read)
                {
                    failure = "自检超时（180 秒）";
                    break;
                }

                string? line = await read.ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                JsonNode? node = TryParse(line);
                if (node is null)
                {
                    continue;
                }

                string ev = StringOf(node, "ev");
                if (ev == RvcRealtimeProtocol.EvSelftest)
                {
                    result = node;
                    break;
                }

                if (ev == RvcRealtimeProtocol.EvError)
                {
                    failure = "自检报错（" + StringOf(node, "stage") + "）：" + StringOf(node, "message");
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is OperationCanceledException)
        {
            failure = "自检读取失败：" + ex.Message;
        }
        finally
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception || ex is NotSupportedException)
            {
                lock (_gate)
                {
                    _stderr.Add("收自检进程：" + ex.Message);
                }
            }

            proc.Dispose();
        }

        if (result is null)
        {
            if (failure.Length == 0)
            {
                failure = "自检没回执";
            }

            string tail = StderrTail();
            return (false, State, failure + (tail.Length > 0 ? "\n" + tail : ""));
        }

        ApplyLatency(result["latency"], 0);
        int actualSr = (int)DoubleOf(result, "sampleRate");
        if (actualSr > 0)
        {
            lock (_gate)
            {
                _state.SampleRate = actualSr;
            }
        }

        double outputSeconds = DoubleOf(result, "outputSeconds");
        double wall = DoubleOf(result, "wallSeconds");
        int deviceIndex = outIndex;

        RvcRealtimeState snapshot = State;
        string message = "自检完成：正弦输出 " + outputSeconds.ToString("F2") + " 秒 / 实耗 " + wall.ToString("F2")
            + " 秒 · 算法延迟 " + snapshot.LatencyMs.ToString("F0") + " 毫秒 · 每块推理 "
            + snapshot.InferMs.ToString("F0") + " 毫秒（不开设备，设备索引 " + deviceIndex + " 未使用）";
        return (true, snapshot, message);
    }

    /// <summary>请求闸——worker 的等待者是单槽，两个请求并发会互相覆盖（连点下拉换音色时会出现），一律串行。</summary>
    private readonly SemaphoreSlim _reqGate = new SemaphoreSlim(1, 1);

    /// <summary>发送一次请求并等指定事件（闸内串行：同一时刻只有一个请求占着等待者）。</summary>
    /// <param name="request">请求体。</param>
    /// <param name="expectEv">期望的事件名。</param>
    /// <param name="timeoutMs">超时毫秒。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 事件节点, 人话)。</returns>
    private async Task<(bool Ok, JsonNode? Node, string Message)> RequestAsync(
        JsonObject request,
        string expectEv,
        int timeoutMs,
        CancellationToken ct)
    {
        await _reqGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RequestCoreAsync(request, expectEv, timeoutMs, ct).ConfigureAwait(false);
        }
        finally
        {
            _reqGate.Release();
        }
    }

    /// <summary>请求实现（调用方必须持有 <see cref="_reqGate"/>）。</summary>
    /// <param name="request">请求体。</param>
    /// <param name="expectEv">期望的事件名。</param>
    /// <param name="timeoutMs">超时毫秒。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 事件节点, 人话)。</returns>
    private async Task<(bool Ok, JsonNode? Node, string Message)> RequestCoreAsync(
        JsonObject request,
        string expectEv,
        int timeoutMs,
        CancellationToken ct)
    {
        Process? proc = _proc;
        if (proc is null)
        {
            return (false, null, "worker 进程不见了");
        }

        var waiter = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _waiter = waiter;
            _waitEv = expectEv;
        }

        try
        {
            string line = request.ToJsonString(WireJsonOptions());
            await proc.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
            await proc.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
        {
            ClearWaiter();
            return (false, null, "写不进 worker：" + ex.Message);
        }

        bool timedOut = false;
        try
        {
            Task finished = await Task.WhenAny(
                waiter.Task,
                Task.Delay(timeoutMs, ct)).ConfigureAwait(false);
            if (finished != waiter.Task)
            {
                timedOut = true;
            }
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
        }

        if (timedOut)
        {
            ClearWaiter();
            string tail = StderrTail();
            return (false, null, "worker 没有回执（等 " + expectEv + " 超时）" + (tail.Length > 0 ? "\n" + tail : ""));
        }

        JsonNode? node = await waiter.Task.ConfigureAwait(false);
        if (node is null)
        {
            return (false, null, "worker 已退出（等 " + expectEv + " 时进程结束）");
        }

        string ev = StringOf(node, "ev");
        if (ev == RvcRealtimeProtocol.EvError)
        {
            string stage = StringOf(node, "stage");
            string message = StringOf(node, "message");
            string trace = StringOf(node, "traceback");
            return (false, node, "worker 报错（" + stage + "）：" + message
                + (trace.Length > 0 ? "\n" + trace : ""));
        }

        if (ev != expectEv)
        {
            return (false, node, "worker 回了意外事件：" + ev);
        }

        return (true, node, "");
    }

    /// <summary>确保 worker 进程已起（起进程 + 泵 stderr）。</summary>
    /// <returns>进程可用返回 true。</returns>
    private bool EnsureProcess()
    {
        if (Alive)
        {
            return true;
        }

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
            lock (_gate)
            {
                _stderr.Add("起进程：" + ex.Message);
            }

            return false;
        }

        lock (_gate)
        {
            _stderr.Clear();
            _proc = proc;
        }

        proc.StandardInput.AutoFlush = true;
        _ = Task.Run(() => DrainStderr(proc));
        _ = Task.Run(() => PumpAsync(proc));
        return true;
    }

    /// <summary>把 worker 脚本写到盘上（不启动进程）；脚本与解释器任一不可用即出声。</summary>
    /// <param name="workerSource">脚本源码（空 = 只查盘上有没有）。</param>
    /// <param name="problem">失败原因。</param>
    /// <param name="changed">脚本内容与盘上不同（或首次落盘）返回 true。</param>
    /// <returns>可用返回 true。</returns>
    private bool EnsureScriptOnDisk(string workerSource, out string problem, out bool changed)
    {
        problem = "";
        changed = false;
        if (_scriptPath.Length == 0)
        {
            problem = "worker 落点不可写（data\\rvc）";
            return false;
        }

        if (!File.Exists(_python))
        {
            problem = "找不到解释器：" + _python + "（本机 ComfyUI 的 python_embeded）";
            return false;
        }

        if (workerSource != null && workerSource.Length > 0)
        {
            try
            {
                // 内容比对：脚本换代而进程还活着时，在跑的 worker 手里仍是旧脚本——由调用方停进程
                changed = !File.Exists(_scriptPath)
                    || File.ReadAllText(_scriptPath) != workerSource;
                if (changed)
                {
                    File.WriteAllText(_scriptPath, workerSource, new UTF8Encoding(false));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                problem = "写 worker 脚本失败：" + ex.Message;
                return false;
            }
        }

        if (!File.Exists(_scriptPath))
        {
            problem = "worker 脚本缺失：" + _scriptPath + "（线 2 的 RvcRealtimeWorkerScript 未落地）";
            return false;
        }

        return true;
    }

    /// <summary>确保脚本在盘上且 worker 进程可用（设备枚举 / 开流的前置）。</summary>
    /// <param name="workerSource">脚本源码（空 = 只查盘上有没有）。</param>
    /// <param name="problem">失败原因。</param>
    /// <returns>可用返回 true。</returns>
    private bool EnsureWorkerReady(string workerSource, out string problem)
    {
        if (!EnsureScriptOnDisk(workerSource, out problem, out bool scriptChanged))
        {
            return false;
        }

        if (scriptChanged && Alive)
        {
            // 脚本内容换代——在跑的进程手里还是旧脚本：停掉，下次开流按新脚本起
            Stop();
        }

        if (!Directory.Exists(_repo))
        {
            problem = "rvc_lite 代码目录不存在：" + _repo;
            return false;
        }

        if (!EnsureProcess())
        {
            string tail = StderrTail();
            problem = "起 worker 失败" + (tail.Length > 0 ? "\n" + tail : "");
            return false;
        }

        return true;
    }

    /// <summary>按配置组 start 请求（嵌套 device + params + bridge，与 worker 侧口径一致）。</summary>
    /// <param name="config">实时配置。</param>
    /// <param name="bridge">桥接用的 VITS 设置（null = 不下发 bridge 节点）。</param>
    /// <returns>请求体。</returns>
    private static JsonObject BuildStartRequest(RvcRealtimeConfig config, RvcVitsBridgeSpec? bridge)
    {
        int inputIndex = -1;
        if (config.Input != null)
        {
            inputIndex = config.Input.Index;
        }

        int outputIndex = -1;
        if (config.Output != null)
        {
            outputIndex = config.Output.Index;
        }

        var device = new JsonObject
        {
            ["hostapi"] = config.HostApiIndex,
            ["input"] = inputIndex,
            ["output"] = outputIndex,
            ["exclusive"] = config.Exclusive,
            ["samplerate"] = config.SampleRate,
        };

        var parameters = new JsonObject
        {
            ["f0Method"] = config.F0Method,
            ["f0UpKey"] = config.F0UpKey,
            ["formant"] = config.Formant,
            ["indexRate"] = config.IndexRate,
            ["rmsMixRate"] = config.RmsMixRate,
            ["protect"] = config.Protect,
            ["threhold"] = config.Threshold,
            ["inputNoiseReduce"] = config.InputDenoise,
            ["outputNoiseReduce"] = config.OutputDenoise,
            ["usePv"] = config.UsePv,
            ["bypass"] = config.Bypass,
            ["sid"] = config.Sid,
            ["blockTime"] = config.BlockTime,
            ["crossfadeTime"] = config.Crossfade,
            ["extraTime"] = config.ExtraTime,
        };

        var request = new JsonObject
        {
            ["cmd"] = RvcRealtimeProtocol.CmdStart,
            ["model"] = config.Model,
            ["device"] = device,
            ["params"] = parameters,
        };

        JsonObject? bridgeNode = BuildBridgeNode(config, bridge);
        if (bridgeNode != null)
        {
            request["bridge"] = bridgeNode;
        }

        return request;
    }
    /// <summary>组桥接节点（VITS 设置 + RVC 侧开关 / 攒块数；模型为空时返回 null，worker 侧不建桥）。</summary>
    /// <param name="config">实时配置。</param>
    /// <param name="bridge">VITS 设置。</param>
    /// <returns>bridge 节点或 null。</returns>
    private static JsonObject? BuildBridgeNode(RvcRealtimeConfig config, RvcVitsBridgeSpec? bridge)
    {
        if (bridge == null)
        {
            return null;
        }

        var node = new JsonObject
        {
            ["enabled"] = config.VcBridge && bridge.Model.Length > 0,
            ["blocks"] = Math.Clamp(config.VcBridgeBlocks, 1, 16),
            ["model"] = bridge.Model,
            ["src"] = bridge.Src,
            ["dst"] = bridge.Dst,
            ["srcAuto"] = bridge.SrcAuto,
            ["f0Auto"] = bridge.F0Auto,
            ["dstF0"] = bridge.DstF0,
            ["f0Profile"] = bridge.F0Profile,
            ["modelsRoot"] = bridge.ModelsRoot,
            ["repo"] = bridge.Repo,
            ["device"] = bridge.Device,
        };
        return node;
    }

    /// <summary>把事件里的延迟分解写进状态（started / latency / selftest 三处共用）。</summary>
    /// <param name="node">延迟对象（blockMs / crossfadeMs / extraMs / deviceMs / totalMs / inferMs）。</param>
    /// <param name="infer">每块推理耗时（毫秒；0 = 用事件里的）。</param>
    private void ApplyLatency(JsonNode? node, double infer)
    {
        if (node is null)
        {
            return;
        }

        double block = DoubleOf(node, "blockMs");
        double crossfade = DoubleOf(node, "crossfadeMs");
        double extra = DoubleOf(node, "extraMs");
        double device = DoubleOf(node, "deviceMs");
        double total = DoubleOf(node, "totalMs");
        double inferMs = DoubleOf(node, "inferMs");
        double bridgeMs = DoubleOf(node, "bridgeMs");

        lock (_gate)
        {
            if (block > 0)
            {
                _state.BlockMs = block;
            }

            if (crossfade > 0)
            {
                _state.CrossfadeMs = crossfade;
            }

            if (extra > 0)
            {
                _state.ExtraMs = extra;
            }

            if (device >= 0)
            {
                _state.DeviceMs = device;
            }

            if (total > 0)
            {
                _state.LatencyMs = total;
            }

            if (inferMs > 0)
            {
                _state.InferMs = inferMs;
            }
            else if (infer > 0)
            {
                _state.InferMs = infer;
            }
        }

        ApplyBridge(node["bridge"]);

        if (bridgeMs > 0)
        {
            lock (_gate)
            {
                _state.BridgeMs = bridgeMs;
            }
        }
    }
    /// <summary>把事件里的桥接状态写进状态（started / latency / updated 三处共用）。</summary>
    /// <param name="node">bridge 对象（null = 桥接没开，清掉标记）。</param>
    private void ApplyBridge(JsonNode? node)
    {
        lock (_gate)
        {
            if (node is null)
            {
                _state.BridgeEnabled = false;
                _state.BridgeState = "";
                _state.BridgeMessage = "";
                _state.BridgeMs = 0;
                _state.BridgeInferMs = 0;
                return;
            }

            _state.BridgeEnabled = BoolOf(node, "enabled");
            _state.BridgeBlocks = (int)DoubleOf(node, "blocks");
            _state.BridgeState = StringOf(node, "state");
            _state.BridgeMessage = StringOf(node, "message");
            _state.BridgeModel = StringOf(node, "model");
            _state.BridgeSrc = (int)DoubleOf(node, "src");
            _state.BridgeDst = (int)DoubleOf(node, "dst");
            _state.BridgeSrcAuto = BoolOf(node, "srcAuto");
            _state.BridgeSrcPicked = (int)DoubleOf(node, "srcPicked");
            _state.BridgeF0In = DoubleOf(node, "f0In");
            _state.BridgeUpKey = DoubleOf(node, "upKey");
            _state.BridgeDstF0 = DoubleOf(node, "dstF0");
            _state.BridgeInferMs = DoubleOf(node, "inferMs");
            _state.BridgeUnderruns = (long)DoubleOf(node, "underruns");
            _state.BridgeMs = DoubleOf(node, "queueMs")
                + DoubleOf(node, "pendingBlocks") * _state.BlockMs
                + _state.BridgeInferMs;

            var log = new List<string>();
            if (node is JsonObject obj && obj.TryGetPropertyValue("log", out JsonNode? logNode)
                && logNode is JsonArray arr)
            {
                foreach (JsonNode? line in arr)
                {
                    if (line is not null)
                    {
                        try
                        {
                            log.Add(line.GetValue<string>());
                        }
                        catch (Exception ex) when (ex is InvalidOperationException || ex is FormatException)
                        {
                            // 单行读不出来就跳过——日志不该把状态整块带塌
                        }
                    }
                }
            }

            _state.BridgeLog = log;
        }
    }

    /// <summary>解析设备清单事件（hostapis + inputs + outputs 三段，按 hostapi 归组）。</summary>
    /// <param name="node">devices 事件节点。</param>
    /// <returns>hostapi 列表（解析失败给空表）。</returns>
    private static List<RvcHostApiItem> ParseDevices(JsonNode? node)
    {
        var result = new List<RvcHostApiItem>();
        if (node is null)
        {
            return result;
        }

        JsonArray? hostApis = node["hostapis"] as JsonArray;
        if (hostApis is not null)
        {
            for (int i = 0; i < hostApis.Count; i++)
            {
                JsonNode? item = hostApis[i];
                if (item is null)
                {
                    continue;
                }

                result.Add(new RvcHostApiItem
                {
                    Index = (int)DoubleOf(item, "index"),
                    Name = StringOf(item, "name"),
                });
            }
        }

        AppendDeviceRows(result, node["inputs"] as JsonArray, true);
        AppendDeviceRows(result, node["outputs"] as JsonArray, false);
        return result;
    }

    /// <summary>把一段设备行挂到对应 hostapi 下（同索引设备只登记一次，输入输出能力按行合并）。</summary>
    /// <param name="hostApis">hostapi 列表。</param>
    /// <param name="rows">设备行数组。</param>
    /// <param name="asInput">这一段是输入清单（true）还是输出清单（false）。</param>
    private static void AppendDeviceRows(List<RvcHostApiItem> hostApis, JsonArray? rows, bool asInput)
    {
        if (rows is null)
        {
            return;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            JsonNode? row = rows[i];
            if (row is null)
            {
                continue;
            }

            int index = (int)DoubleOf(row, "index");
            int hostApi = (int)DoubleOf(row, "hostapi");
            string name = StringOf(row, "name");
            bool cable = BoolOf(row, "cable") || VbCableService.IsCable(name);

            RvcHostApiItem? owner = null;
            for (int j = 0; j < hostApis.Count; j++)
            {
                if (hostApis[j].Index == hostApi)
                {
                    owner = hostApis[j];
                    break;
                }
            }

            if (owner == null)
            {
                owner = new RvcHostApiItem
                {
                    Index = hostApi,
                    Name = "hostapi " + hostApi,
                };
                hostApis.Add(owner);
            }

            RvcDeviceItem? device = null;
            for (int j = 0; j < owner.Devices.Count; j++)
            {
                if (owner.Devices[j].Index == index)
                {
                    device = owner.Devices[j];
                    break;
                }
            }

            if (device == null)
            {
                device = new RvcDeviceItem
                {
                    Index = index,
                    Name = name,
                    Cable = cable,
                };
                owner.Devices.Add(device);
            }

            if (asInput)
            {
                device.In = true;
            }
            else
            {
                device.Out = true;
            }
        }
    }

    /// <summary>事件泵——持续读 stdout 行，更新状态并唤醒等待者。</summary>
    /// <param name="proc">worker 进程。</param>
    /// <returns>无（进程退出即结束）。</returns>
    private async Task PumpAsync(Process proc)
    {
        try
        {
            while (true)
            {
                string? line = await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                JsonNode? node = TryParse(line);
                string ev = StringOf(node, "ev");
                if (ev.Length == 0)
                {
                    continue;
                }

                if (ev == RvcRealtimeProtocol.EvLevel)
                {
                    lock (_gate)
                    {
                        _state.LevelInDb = DoubleOf(node, "inDb");
                        _state.LevelOutDb = DoubleOf(node, "outDb");
                    }

                    continue;
                }

                if (ev == RvcRealtimeProtocol.EvLatency)
                {
                    ApplyLatency(node, 0);
                    continue;
                }

                if (ev == RvcRealtimeProtocol.EvReady)
                {
                    string torch = StringOf(node, "torch");
                    string device = StringOf(node, "device");
                    bool half = BoolOf(node, "half");
                    lock (_gate)
                    {
                        _state.Worker = "torch " + torch + " · " + device + " · "
                            + (half ? "半精度" : "全精度");
                    }
                }

                if (ev == RvcRealtimeProtocol.EvError)
                {
                    string message = StringOf(node, "message");
                    string stage = StringOf(node, "stage");
                    lock (_gate)
                    {
                        _state.State = "error";
                        _state.Message = "worker 报错（" + stage + "）：" + message;
                    }
                }

                if (ev == RvcRealtimeProtocol.EvStarted)
                {
                    string model = StringOf(node, "model");
                    lock (_gate)
                    {
                        _state.State = "running";
                        if (model.Length > 0)
                        {
                            _state.Model = model;
                        }
                    }

                    ApplyLatency(node["latency"], 0);
                    ApplyBridge(node["bridge"]);
                }

                if (ev == RvcRealtimeProtocol.EvStopped)
                {
                    lock (_gate)
                    {
                        _state.State = "stopped";
                    }
                }

                CompleteWaiter(ev, node);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException || ex is OperationCanceledException)
        {
            lock (_gate)
            {
                _stderr.Add("事件泵：" + ex.Message);
            }
        }

        lock (_gate)
        {
            _proc = null;
            if (_state.State == "running" || _state.State == "starting")
            {
                _state.State = "stopped";
                _state.Message = "worker 已退出";
            }
        }

        CompleteWaiter("", null);
    }

    /// <summary>把事件交给等待者（事件名对得上才唤醒）。</summary>
    /// <param name="ev">事件名。</param>
    /// <param name="node">事件节点（null = 进程退出）。</param>
    private void CompleteWaiter(string ev, JsonNode? node)
    {
        TaskCompletionSource<JsonNode?>? waiter;
        lock (_gate)
        {
            waiter = _waiter;
            if (waiter is null)
            {
                return;
            }

            if (node != null && ev != _waitEv && ev != RvcRealtimeProtocol.EvError)
            {
                return;
            }

            _waiter = null;
            _waitEv = "";
        }

        waiter.TrySetResult(node);
    }

    /// <summary>清掉当前等待者（超时 / 发送失败时调用）。</summary>
    private void ClearWaiter()
    {
        lock (_gate)
        {
            _waiter = null;
            _waitEv = "";
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

                    _state.StderrTail = new List<string>(_stderr);
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

        CompleteWaiter("", null);
        SetState("stopped", "实时 worker 已停止");
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

    /// <summary>行协议序列化选项（不转义中文——路径里常有）。</summary>
    /// <returns>选项。</returns>
    private static JsonSerializerOptions WireJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        return options;
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
        catch (JsonException)
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
    /// <returns>值（缺失给 false）。</returns>
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

    /// <summary>取数字字段。</summary>
    /// <param name="node">节点。</param>
    /// <param name="name">字段名。</param>
    /// <returns>值（缺失给 0）。</returns>
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
