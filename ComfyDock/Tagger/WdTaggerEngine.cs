using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Tagger;

/// <summary>常驻打标引擎的运行态——面板照它显示这一路的死活。</summary>
public sealed class TaggerEngineState
{
    /// <summary>状态：stopped / starting / ready / busy / error。</summary>
    public string State { get; set; } = "stopped";

    /// <summary>人话说明。</summary>
    public string Message { get; set; } = "";

    /// <summary>worker 的 onnxruntime 版本（就绪后才有）。</summary>
    public string OnnxRuntime { get; set; } = "";

    /// <summary>可用的执行提供器（就绪后才有，逗号分隔）。</summary>
    public string Providers { get; set; } = "";

    /// <summary>是否走 CUDA。</summary>
    public bool Cuda { get; set; }

    /// <summary>worker 初始化耗时（秒）。</summary>
    public double StartupSeconds { get; set; }

    /// <summary>本次启动时刻。</summary>
    public string StartedAt { get; set; } = "";
}

/// <summary>
/// 打标引擎——用 ComfyUI 的便携解释器（<c>python_embeded</c>）跑一个常驻 worker。
/// <para>只借它的推理环境（onnxruntime + numpy + PIL），<b>不启动 ComfyUI 服务</b>：没有 8188 端口、没有界面，
/// 所以「ComfyUI 没开」也能打标。模型仍读 ComfyUI 的模型目录（<c>models\onnx\wd-tagger\</c>）。</para>
/// <para>孤儿防护：worker 从 stdin 读请求，父进程消失（含被强杀）即读到 EOF 自行退出，不留僵尸进程。</para>
/// </summary>
public sealed class WdTaggerEngine : IDisposable
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
    private TaggerEngineState _state = new TaggerEngineState();

    /// <summary>是否已释放。</summary>
    private bool _disposed;

    /// <summary>当前状态（快照副本）。</summary>
    public TaggerEngineState State
    {
        get
        {
            lock (_gate)
            {
                return new TaggerEngineState
                {
                    State = _state.State,
                    Message = _state.Message,
                    OnnxRuntime = _state.OnnxRuntime,
                    Providers = _state.Providers,
                    Cuda = _state.Cuda,
                    StartupSeconds = _state.StartupSeconds,
                    StartedAt = _state.StartedAt,
                };
            }
        }
    }

    /// <summary>解释器路径（面板显示用）。</summary>
    public string PythonPath => _python;

    /// <summary>worker 脚本落点。</summary>
    public string ScriptPath => _scriptPath;

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

        string dataRoot = Path.Combine(AppContext.BaseDirectory, "data", "tagger");
        try
        {
            Directory.CreateDirectory(dataRoot);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _scriptPath = "";
            return false;
        }

        _scriptPath = Path.Combine(dataRoot, "wd_tagger_worker.py");
        return File.Exists(_python);
    }

    /// <summary>确保 worker 已就绪——没起就起，起了直接返回。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否就绪, 说明)。</returns>
    public async Task<(bool Ok, string Message)> EnsureStartedAsync(CancellationToken ct)
    {
        if (Alive)
        {
            return (true, "打标引擎已在跑");
        }

        if (_scriptPath.Length == 0)
        {
            return (false, "打标引擎落点不可写（data\\tagger）");
        }

        if (!File.Exists(_python))
        {
            return (false, "找不到引擎解释器：" + _python + "（本机不是便携整合包？）");
        }

        try
        {
            File.WriteAllText(_scriptPath, TaggerWorkerScript.Source, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return (false, "写 worker 脚本失败：" + ex.Message);
        }

        SetState("starting", "正在起打标引擎…");

        var psi = new ProcessStartInfo
        {
            FileName = _python,
            Arguments = "-s \"" + _scriptPath + "\"",
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
            SetState("error", "起打标引擎失败：" + ex.Message);
            return (false, "起打标引擎失败：" + ex.Message);
        }

        lock (_gate)
        {
            _stderr.Clear();
            _proc = proc;
        }

        proc.StandardInput.AutoFlush = true;
        _ = Task.Run(() => DrainStderr(proc));

        // onnxruntime + numpy + PIL 导入约 3~10 秒——给 120 秒余量
        string? line = await ReadLineWithTimeoutAsync(proc, 120000, ct).ConfigureAwait(false);
        if (line is null)
        {
            string tail = StderrTail();
            Stop();
            SetState("error", "打标引擎没在 2 分钟内就绪");
            return (false, "打标引擎没在 2 分钟内就绪" + (tail.Length > 0 ? "\n" + tail : ""));
        }

        JsonNode? node = TryParse(line);
        string ev = StringOf(node, "ev");
        if (ev == "error")
        {
            string message = StringOf(node, "message");
            string trace = StringOf(node, "traceback");
            Stop();
            SetState("error", "打标引擎启动失败：" + message);
            return (false, "打标引擎启动失败：" + message + (trace.Length > 0 ? "\n" + trace : ""));
        }

        if (ev != "ready")
        {
            Stop();
            SetState("error", "打标引擎回了意外事件：" + ev);
            return (false, "打标引擎回了意外事件：" + ev + "（" + line + "）");
        }

        string ort = StringOf(node, "onnxruntime");
        string providers = StringOf(node, "providers");
        bool cuda = providers.Contains("CUDA", StringComparison.OrdinalIgnoreCase);

        lock (_gate)
        {
            _state.State = "ready";
            _state.OnnxRuntime = ort;
            _state.Providers = providers;
            _state.Cuda = cuda;
            _state.StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _state.Message = "打标引擎就绪（onnxruntime " + ort + (cuda ? " · CUDA 可用" : " · 仅 CPU") + "）";
        }

        return (true, "打标引擎就绪（onnxruntime " + ort + (cuda ? " · CUDA" : " · CPU") + "）");
    }

    /// <summary>跑一批图片——逐张回调，产物 txt 由 worker 侧写。</summary>
    /// <param name="onnxPath">权重完整路径。</param>
    /// <param name="labelsPath">标签表完整路径。</param>
    /// <param name="modelId">模型 id（仅回显）。</param>
    /// <param name="images">待打标图片完整路径。</param>
    /// <param name="p">参数（阈值 / MCut / 设备 / 是否写 txt）。</param>
    /// <param name="onItem">每张图处理完的回调。</param>
    /// <param name="onFailed">单张失败的回调（文件名, 原因）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 说明, 总耗时秒)。</returns>
    public async Task<(bool Ok, string Message, double Seconds)> RunAsync(
        string onnxPath,
        string labelsPath,
        string modelId,
        List<string> images,
        TaggerGenParams p,
        Action<TaggerImageResult> onItem,
        Action<string, string> onFailed,
        CancellationToken ct)
    {
        (bool Ok, string Message) started = await EnsureStartedAsync(ct).ConfigureAwait(false);
        if (!started.Ok)
        {
            return (false, started.Message, 0);
        }

        Process? proc = _proc;
        if (proc is null)
        {
            return (false, "打标引擎进程不见了", 0);
        }

        lock (_gate)
        {
            if (_state.State == "busy")
            {
                return (false, "打标引擎上一批还在跑", 0);
            }

            _state.State = "busy";
            _state.Message = "打标进行中…";
        }

        string id = Guid.NewGuid().ToString("N");
        var request = new JsonObject
        {
            ["cmd"] = "run",
            ["id"] = id,
            ["modelId"] = modelId,
            ["onnx"] = onnxPath,
            ["labels"] = labelsPath,
            ["generalThreshold"] = p.GeneralThreshold,
            ["characterThreshold"] = p.CharacterThreshold,
            ["generalMcut"] = p.GeneralMcut,
            ["characterMcut"] = p.CharacterMcut,
            ["device"] = p.Device,
            ["writeText"] = p.WriteText,
        };

        var array = new JsonArray();
        foreach (string image in images)
        {
            array.Add(image);
        }

        request["images"] = array;

        try
        {
            await proc.StandardInput.WriteLineAsync(request.ToJsonString()).ConfigureAwait(false);
            await proc.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
        {
            SetState("error", "写不进打标引擎：" + ex.Message);
            return (false, "写不进打标引擎：" + ex.Message, 0);
        }

        // 单张最长 5 分钟（CPU 上大图也就几秒，留足余量）；整批靠调用方的取消令牌收口
        while (true)
        {
            string? line = await ReadLineWithTimeoutAsync(proc, 300000, ct).ConfigureAwait(false);
            if (line is null)
            {
                string tail = StderrTail();
                SetState("error", "打标引擎没有回执");
                return (false, "打标引擎没有回执" + (tail.Length > 0 ? "\n" + tail : ""), 0);
            }

            JsonNode? node = TryParse(line);
            string ev = StringOf(node, "ev");
            if (ev == "pong" || ev == "start")
            {
                continue;
            }

            if (ev == "item")
            {
                onItem(ToResult(node));
                continue;
            }

            if (ev == "itemerror")
            {
                onFailed(StringOf(node, "file"), StringOf(node, "message"));
                continue;
            }

            if (ev == "error")
            {
                string message = StringOf(node, "message");
                string stage = StringOf(node, "stage");
                string trace = StringOf(node, "traceback");
                SetState("ready", "上一次失败：" + message);
                return (false, "打标引擎报错（" + stage + "）：" + message
                    + (trace.Length > 0 ? "\n" + trace : ""), 0);
            }

            if (ev != "done")
            {
                continue;
            }

            double seconds = DoubleOf(node, "seconds");
            SetState("ready", "打标引擎空闲（上一批 " + seconds.ToString("F1") + " 秒）");
            return (true, "打标完成（" + seconds.ToString("F1") + " 秒）", seconds);
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

        SetState("stopped", "打标引擎已停止");
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

    /// <summary>把 worker 回的一条 item 事件转成结果对象。</summary>
    /// <param name="node">事件节点。</param>
    /// <returns>结果。</returns>
    private static TaggerImageResult ToResult(JsonNode? node)
    {
        var result = new TaggerImageResult
        {
            File = StringOf(node, "file"),
            Path = StringOf(node, "path"),
            Rating = StringOf(node, "rating"),
            GeneralText = StringOf(node, "generalText"),
            TextFile = StringOf(node, "textFile"),
            Seconds = DoubleOf(node, "seconds"),
        };

        foreach (string key in new[] { "ratings", "general", "characters" })
        {
            JsonArray? list = node is null ? null : node[key] as JsonArray;
            if (list is null)
            {
                continue;
            }

            foreach (JsonNode? item in list)
            {
                if (item is null)
                {
                    continue;
                }

                var tag = new TaggerTag
                {
                    Name = StringOf(item, "name"),
                    Score = DoubleOf(item, "score"),
                };

                if (key == "ratings")
                {
                    result.Ratings.Add(tag);
                }
                else if (key == "characters")
                {
                    result.Characters.Add(tag);
                }
                else
                {
                    result.General.Add(tag);
                }
            }
        }

        return result;
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
