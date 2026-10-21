using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Vits;

/// <summary>
/// VITS 常驻引擎的运行态——面板照它显示这一路的死活。
/// </summary>
public sealed class VitsEngineState
{
    /// <summary>状态：stopped / starting / ready / busy / error。</summary>
    public string State { get; set; } = "stopped";

    /// <summary>人话说明。</summary>
    public string Message { get; set; } = "";

    /// <summary>推理设备（cuda / cpu）。</summary>
    public string Device { get; set; } = "";

    /// <summary>torch 是否看到 CUDA。</summary>
    public bool Cuda { get; set; }

    /// <summary>当前加载的模型名（空 = 还没 load）。</summary>
    public string Model { get; set; } = "";

    /// <summary>当前模型的原生采样率。</summary>
    public int SamplingRate { get; set; }

    /// <summary>当前模型的说话人数。</summary>
    public int Speakers { get; set; }

    /// <summary>就绪时刻。</summary>
    public string StartedAt { get; set; } = "";

    /// <summary>启动到就绪用了多少秒。</summary>
    public double StartupSeconds { get; set; }

    /// <summary>解释器路径（面板显示用）。</summary>
    public string Python { get; set; } = "";

    /// <summary>worker 脚本路径。</summary>
    public string Script { get; set; } = "";

    /// <summary>worker 进程是否还活着。</summary>
    public bool Alive { get; set; }
}

/// <summary>
/// 批量合成里的一条结果——某个说话人的出声成败（单条失败不牵连整批）。
/// </summary>
public sealed class VitsBatchItem
{
    /// <summary>说话人索引。</summary>
    public int Sid { get; set; }

    /// <summary>产物完整路径（失败时也回传请求路径，便于对账）。</summary>
    public string Out { get; set; } = "";

    /// <summary>是否成功。</summary>
    public bool Ok { get; set; }

    /// <summary>音频秒数。</summary>
    public double Seconds { get; set; }

    /// <summary>喂进模型的 token 数。</summary>
    public int Tokens { get; set; }

    /// <summary>这条耗时毫秒。</summary>
    public long Ms { get; set; }

    /// <summary>失败原因（成功为空）。</summary>
    public string Error { get; set; } = "";
}

/// <summary>
/// VITS 常驻引擎——用本机 ComfyUI 的 python_embeded 跑 <c>runtime\vits\vits_worker.py</c>。
/// 行协议：stdin 一行 JSON 请求 → stdout 一行 JSON 响应；stderr 只作诊断尾巴。
/// </summary>
public sealed class VitsEngine : IDisposable
{
    /// <summary>状态锁。</summary>
    private readonly object _gate = new object();

    /// <summary>请求串行闸——一条请求等到回执才发下一条。</summary>
    private readonly SemaphoreSlim _turn = new SemaphoreSlim(1, 1);

    /// <summary>worker 的 stderr 尾巴（出错时给人看）。</summary>
    private readonly List<string> _stderr = new List<string>();

    /// <summary>worker 进程。</summary>
    private Process? _proc;

    /// <summary>解释器路径。</summary>
    private string _python = "";

    /// <summary>worker 脚本路径。</summary>
    private string _script = "";

    /// <summary>模型根目录（<c>ComfyUI\models\VITS</c>）。</summary>
    private string _modelsRoot = "";

    /// <summary>推理设备（auto / cuda / cpu）。</summary>
    private string _device = "auto";

    /// <summary>当前状态。</summary>
    private VitsEngineState _state = new VitsEngineState();

    /// <summary>是否已释放。</summary>
    private bool _disposed;

    /// <summary>当前状态（快照副本）。</summary>
    public VitsEngineState State
    {
        get
        {
            lock (_gate)
            {
                return new VitsEngineState
                {
                    State = _state.State,
                    Message = _state.Message,
                    Device = _state.Device,
                    Cuda = _state.Cuda,
                    Model = _state.Model,
                    SamplingRate = _state.SamplingRate,
                    Speakers = _state.Speakers,
                    StartedAt = _state.StartedAt,
                    StartupSeconds = _state.StartupSeconds,
                    Python = _state.Python,
                    Script = _state.Script,
                    Alive = Alive,
                };
            }
        }
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

    /// <summary>设定这一代 worker 的启动参数（只看磁盘，不启动进程）。</summary>
    /// <param name="python">解释器。</param>
    /// <param name="script">worker 脚本。</param>
    /// <param name="modelsRoot">模型根目录。</param>
    /// <param name="device">推理设备。</param>
    public void Prepare(string python, string script, string modelsRoot, string device)
    {
        bool changed;
        lock (_gate)
        {
            changed = _python != python || _script != script || _modelsRoot != modelsRoot || _device != device;
            _python = python;
            _script = script;
            _modelsRoot = modelsRoot;
            _device = device;
            _state.Python = python;
            _state.Script = script;
        }

        if (changed && Alive)
        {
            // 路径或设备变了——旧进程不认识新参数，停掉等下次按新参数起
            Stop();
        }
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

        string python;
        string script;
        string modelsRoot;
        string device;
        lock (_gate)
        {
            python = _python;
            script = _script;
            modelsRoot = _modelsRoot;
            device = _device;
        }

        if (script.Length == 0 || !File.Exists(script))
        {
            return (false, "worker 脚本不存在：" + script + "（确认 runtime\\vits 随包发布）");
        }

        if (!File.Exists(python))
        {
            return (false, "找不到解释器：" + python + "（指向 ComfyUI 的 python_embeded）");
        }

        SetState("starting", "正在起 worker…");

        var psi = new ProcessStartInfo
        {
            FileName = python,
            Arguments = "\"" + script + "\" --models-root \"" + modelsRoot + "\" --device \"" + device + "\"",
            WorkingDirectory = Path.GetDirectoryName(script) ?? ".",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // 子进程三件套统一 UTF-8：否则 Windows 上按本地代码页解码，日文文本变代理字符
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

        Stopwatch watch = Stopwatch.StartNew();
        string? line = await ReadLineWithTimeoutAsync(proc, 120000, ct).ConfigureAwait(false);
        if (line is null)
        {
            string tail = StderrTail();
            Stop();
            SetState("error", "worker 没在 2 分钟内就绪");
            return (false, "worker 没在 2 分钟内就绪" + (tail.Length > 0 ? "\n" + tail : ""));
        }

        JsonNode? node = TryParse(line);
        if (node is null || !BoolOf(node, "ready"))
        {
            Stop();
            SetState("error", "worker 回了意外首行");
            return (false, "worker 回了意外首行：" + line);
        }

        string dev = StringOf(node, "device");
        lock (_gate)
        {
            _state.State = "ready";
            _state.Device = dev;
            _state.Cuda = dev.StartsWith("cuda", StringComparison.OrdinalIgnoreCase);
            _state.StartupSeconds = Math.Round(watch.Elapsed.TotalSeconds, 2);
            _state.StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _state.Message = "引擎就绪（" + _state.StartupSeconds.ToString("F1") + " 秒 · " + dev + "）";
        }

        return (true, _state.Message);
    }

    /// <summary>加载一个模型（同名模型重复调用直接复用）。</summary>
    /// <param name="model">模型名（文件名去后缀）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 说明, 回执)。</returns>
    public async Task<(bool Ok, string Message, JsonNode? Node)> LoadAsync(string model, CancellationToken ct)
    {
        (bool Ok, string Message) started = await EnsureStartedAsync(ct).ConfigureAwait(false);
        if (!started.Ok)
        {
            return (false, started.Message, null);
        }

        lock (_gate)
        {
            if (_state.State == "busy")
            {
                return (false, "上一件还在跑", null);
            }

            if (_state.Model == model && _state.SamplingRate > 0)
            {
                return (true, "模型已在跑：" + model, null);
            }

            _state.State = "busy";
            _state.Message = "正在加载模型 " + model + "…";
        }

        var request = new JsonObject
        {
            ["op"] = "load",
            ["model"] = model,
        };

        (JsonNode? node, string error) = await ExchangeAsync(request, 180000, ct).ConfigureAwait(false);
        if (node is null)
        {
            SetState("error", "加载模型失败：" + error);
            return (false, "加载模型失败：" + error, null);
        }

        lock (_gate)
        {
            _state.State = "ready";
            _state.Model = StringOf(node, "model");
            _state.SamplingRate = (int)DoubleOf(node, "sampling_rate");
            _state.Speakers = (int)DoubleOf(node, "n_speakers");
            _state.Device = StringOf(node, "device");
            _state.Message = "已加载 " + _state.Model + "（" + _state.Speakers + " 个说话人 · "
                + _state.SamplingRate + " Hz）";
        }

        return (true, _state.Message, node);
    }

    /// <summary>清理预览——文本走 cleaners 后得到的音素串。</summary>
    /// <param name="text">原始文本。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(清理结果, 错误说明)。</returns>
    public async Task<(string Cleaned, string Error)> CleanAsync(string text, CancellationToken ct)
    {
        var request = new JsonObject
        {
            ["op"] = "clean",
            ["text"] = text,
        };

        (JsonNode? node, string error) = await ExchangeAsync(request, 120000, ct).ConfigureAwait(false);
        if (node is null)
        {
            return ("", error);
        }

        return (StringOf(node, "cleaned"), "");
    }

    /// <summary>合成一句。</summary>
    /// <param name="text">文本（或音素串）。</param>
    /// <param name="sid">说话人索引。</param>
    /// <param name="lengthScale">语速（1 = 原速）。</param>
    /// <param name="noiseScale">音色噪声。</param>
    /// <param name="noiseScaleW">时长噪声。</param>
    /// <param name="cleaned">文本已是音素串（符号输入）。</param>
    /// <param name="outPath">产物路径。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 产物路径, 音频秒数, tokens, 说明)。</returns>
    public async Task<(bool Ok, string File, double AudioSeconds, int Tokens, string Message)> TtsAsync(
        string text,
        int sid,
        double lengthScale,
        double noiseScale,
        double noiseScaleW,
        bool cleaned,
        string outPath,
        CancellationToken ct)
    {
        var request = new JsonObject
        {
            ["op"] = "tts",
            ["text"] = text,
            ["sid"] = sid,
            ["length_scale"] = lengthScale,
            ["noise_scale"] = noiseScale,
            ["noise_scale_w"] = noiseScaleW,
            ["cleaned"] = cleaned,
            ["out"] = outPath,
        };

        return await RunAudioAsync(request, 600000, ct).ConfigureAwait(false);
    }

    /// <summary>同一条文本批量跑多个说话人——一次请求、一次模型加载、一次文本前端。</summary>
    /// <param name="text">文本（或音素串）。</param>
    /// <param name="sids">说话人索引清单。</param>
    /// <param name="outPaths">与 sids 一一对应的产物路径。</param>
    /// <param name="lengthScale">语速（1 = 原速）。</param>
    /// <param name="noiseScale">音色噪声。</param>
    /// <param name="noiseScaleW">时长噪声。</param>
    /// <param name="cleaned">文本已是音素串（符号输入）。</param>
    /// <param name="onItem">每完成一条的回调（该条结果, 已完成, 总数）——空则不报进度。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 说明, 逐条结果)。</returns>
    public async Task<(bool Ok, string Message, List<VitsBatchItem> Items)> TtsBatchAsync(
        string text,
        IReadOnlyList<int> sids,
        IReadOnlyList<string> outPaths,
        double lengthScale,
        double noiseScale,
        double noiseScaleW,
        bool cleaned,
        Action<VitsBatchItem, int, int>? onItem,
        CancellationToken ct)
    {
        if (sids.Count == 0)
        {
            return (false, "没有勾选任何说话人", new List<VitsBatchItem>());
        }

        if (outPaths.Count != sids.Count)
        {
            return (false, "产物路径数与说话人数不一致", new List<VitsBatchItem>());
        }

        (bool Ok, string Message) started = await EnsureStartedAsync(ct).ConfigureAwait(false);
        if (!started.Ok)
        {
            return (false, started.Message, new List<VitsBatchItem>());
        }

        lock (_gate)
        {
            if (_state.State == "busy")
            {
                return (false, "上一件还在跑", new List<VitsBatchItem>());
            }

            _state.State = "busy";
            _state.Message = "批量合成中…";
        }

        var request = new JsonObject
        {
            ["op"] = "tts_batch",
            ["text"] = text,
            ["length_scale"] = lengthScale,
            ["noise_scale"] = noiseScale,
            ["noise_scale_w"] = noiseScaleW,
            ["cleaned"] = cleaned,
        };

        var items = new JsonArray();
        for (int i = 0; i < sids.Count; i++)
        {
            items.Add(new JsonObject
            {
                ["sid"] = sids[i],
                ["out"] = outPaths[i],
            });
        }

        request["items"] = items;

        (List<VitsBatchItem> done, string error) = await ExchangeBatchAsync(request, 600000, sids.Count, onItem, ct)
            .ConfigureAwait(false);
        if (error.Length > 0)
        {
            SetState("error", error);
            return (false, error, done);
        }

        SetState("ready", "引擎空闲");
        int okCount = 0;
        foreach (VitsBatchItem item in done)
        {
            if (item.Ok)
            {
                okCount++;
            }
        }

        return (true, "批量出声 " + okCount + "/" + sids.Count, done);
    }

    /// <summary>把一段音频换成目标说话人的音色。</summary>
    /// <param name="audioPath">输入音频。</param>
    /// <param name="src">原说话人索引。</param>
    /// <param name="dst">目标说话人索引。</param>
    /// <param name="outPath">产物路径。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 产物路径, 音频秒数, tokens, 说明)。</returns>
    public async Task<(bool Ok, string File, double AudioSeconds, int Tokens, string Message)> VcAsync(
        string audioPath,
        int src,
        int dst,
        string outPath,
        CancellationToken ct)
    {
        var request = new JsonObject
        {
            ["op"] = "vc",
            ["audio"] = audioPath,
            ["src"] = src,
            ["dst"] = dst,
            ["out"] = outPath,
        };

        return await RunAudioAsync(request, 600000, ct).ConfigureAwait(false);
    }

    /// <summary>停掉 worker（发 stop，4 秒不退就强杀）。</summary>
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
                    proc.StandardInput.WriteLine("{\"op\":\"stop\"}");
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

        lock (_gate)
        {
            _state.Model = "";
            _state.SamplingRate = 0;
            _state.Speakers = 0;
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
        _turn.Dispose();
    }

    /// <summary>发一条音频类请求并解析回执（tts / vc 共用）。</summary>
    /// <param name="request">请求体。</param>
    /// <param name="timeoutMs">超时毫秒。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 产物, 音频秒数, tokens, 说明)。</returns>
    private async Task<(bool Ok, string File, double AudioSeconds, int Tokens, string Message)> RunAudioAsync(
        JsonObject request,
        int timeoutMs,
        CancellationToken ct)
    {
        (bool Ok, string Message) started = await EnsureStartedAsync(ct).ConfigureAwait(false);
        if (!started.Ok)
        {
            return (false, "", 0, 0, started.Message);
        }

        lock (_gate)
        {
            if (_state.State == "busy")
            {
                return (false, "", 0, 0, "上一件还在跑");
            }

            _state.State = "busy";
            _state.Message = request["op"]?.GetValue<string>() == "vc" ? "换音色中…" : "合成中…";
        }

        (JsonNode? node, string error) = await ExchangeAsync(request, timeoutMs, ct).ConfigureAwait(false);
        SetState("ready", "引擎空闲");
        if (node is null)
        {
            SetState("error", error);
            return (false, "", 0, 0, error);
        }

        string file = StringOf(node, "out");
        double audioSeconds = DoubleOf(node, "seconds");
        int tokens = (int)DoubleOf(node, "tokens");
        if (file.Length == 0)
        {
            return (false, "", 0, 0, "引擎跑完了，但没拿到产物路径");
        }

        return (true, file, audioSeconds, tokens, "出声 " + audioSeconds.ToString("F1") + " 秒");
    }

    /// <summary>发一条批量请求，逐行收 item 回执直到 stage=done。</summary>
    /// <param name="request">请求体。</param>
    /// <param name="timeoutMs">每行等待超时毫秒（不是整批总时长）。</param>
    /// <param name="total">总条数（报进度用）。</param>
    /// <param name="onItem">每完成一条的回调。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(逐条结果, 错误说明)。</returns>
    private async Task<(List<VitsBatchItem> Items, string Error)> ExchangeBatchAsync(
        JsonObject request,
        int timeoutMs,
        int total,
        Action<VitsBatchItem, int, int>? onItem,
        CancellationToken ct)
    {
        var items = new List<VitsBatchItem>();
        Process? proc = _proc;
        if (proc is null)
        {
            return (items, "引擎进程不见了");
        }

        await _turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                await proc.StandardInput.WriteLineAsync(request.ToJsonString()).ConfigureAwait(false);
                await proc.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                return (items, "写不进引擎：" + ex.Message);
            }

            while (true)
            {
                string? line = await ReadLineWithTimeoutAsync(proc, timeoutMs, ct).ConfigureAwait(false);
                if (line is null)
                {
                    string tail = StderrTail();
                    return (items, "引擎没有回执" + (tail.Length > 0 ? "\n" + tail : ""));
                }

                JsonNode? node = TryParse(line);
                if (node is null)
                {
                    return (items, "回执不是合法 JSON：" + line);
                }

                if (!BoolOf(node, "ok"))
                {
                    string err = StringOf(node, "error");
                    return (items, err.Length > 0 ? err : "引擎报错（无说明）");
                }

                string stage = StringOf(node, "stage");
                if (stage == "item")
                {
                    var item = new VitsBatchItem
                    {
                        Sid = (int)DoubleOf(node, "sid"),
                        Out = StringOf(node, "out"),
                        Seconds = DoubleOf(node, "seconds"),
                        Tokens = (int)DoubleOf(node, "tokens"),
                        Ms = (long)DoubleOf(node, "ms"),
                        Error = StringOf(node, "error"),
                    };
                    item.Ok = item.Error.Length == 0;
                    items.Add(item);
                    onItem?.Invoke(item, items.Count, total);
                    continue;
                }

                if (stage == "done")
                {
                    return (items, "");
                }

                return (items, "批量回执形态意外：" + line);
            }
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>发一条请求、读一条回执。</summary>
    /// <param name="request">请求体。</param>
    /// <param name="timeoutMs">超时毫秒。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(回执节点, 错误说明)。</returns>
    private async Task<(JsonNode? Node, string Error)> ExchangeAsync(JsonObject request, int timeoutMs, CancellationToken ct)
    {
        Process? proc = _proc;
        if (proc is null)
        {
            return (null, "引擎进程不见了");
        }

        await _turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                await proc.StandardInput.WriteLineAsync(request.ToJsonString()).ConfigureAwait(false);
                await proc.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                return (null, "写不进引擎：" + ex.Message);
            }

            string? line = await ReadLineWithTimeoutAsync(proc, timeoutMs, ct).ConfigureAwait(false);
            if (line is null)
            {
                string tail = StderrTail();
                return (null, "引擎没有回执" + (tail.Length > 0 ? "\n" + tail : ""));
            }

            JsonNode? node = TryParse(line);
            if (node is null)
            {
                return (null, "回执不是合法 JSON：" + line);
            }

            if (!BoolOf(node, "ok"))
            {
                string err = StringOf(node, "error");
                return (null, err.Length > 0 ? err : "引擎报错（无说明）");
            }

            return (node, "");
        }
        finally
        {
            _turn.Release();
        }
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

    /// <summary>把 worker 的 stderr 收进环形尾巴。</summary>
    /// <param name="proc">worker 进程。</param>
    private void DrainStderr(Process proc)
    {
        try
        {
            while (true)
            {
                string? line = proc.StandardError.ReadLine();
                if (line is null)
                {
                    return;
                }

                lock (_gate)
                {
                    _stderr.Add(line);
                    while (_stderr.Count > 200)
                    {
                        _stderr.RemoveAt(0);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
        {
            lock (_gate)
            {
                _stderr.Add("读 stderr 中断：" + ex.Message);
            }
        }
    }

    /// <summary>stderr 尾巴（最后 20 行）。</summary>
    /// <returns>尾巴文本。</returns>
    private string StderrTail()
    {
        lock (_gate)
        {
            int start = Math.Max(0, _stderr.Count - 20);
            return string.Join("\n", _stderr.GetRange(start, _stderr.Count - start));
        }
    }

    /// <summary>更新状态。</summary>
    /// <param name="state">状态名。</param>
    /// <param name="message">说明。</param>
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
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(name, out JsonNode? value) || value is null)
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
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(name, out JsonNode? value) || value is null)
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
    /// <returns>值（缺失给 0）。</returns>
    private static double DoubleOf(JsonNode? node, string name)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(name, out JsonNode? value) || value is null)
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
