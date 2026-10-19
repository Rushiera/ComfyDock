using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Collect;

/// <summary>
/// 套壳浏览器客户端——拉起并驱动 <c>ComfyDock.Shell.exe</c>。
/// 通道是标准输入输出的**行协议**（不占端口）：一行一条请求、一行一条应答；
/// 套壳进程第一次启动会先发一条 <c>{"ev":"ready"}</c>，客户端自动跳过。
/// </summary>
public sealed class ShellClient : IAsyncDisposable
{
    /// <summary>请求序列化选项。</summary>
    private static readonly JsonSerializerOptions RequestOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>套壳可执行文件完整路径。</summary>
    private readonly string _exePath;

    /// <summary>数据根（套壳读写同一份账号表与登录态）。</summary>
    private readonly string _dataDir;

    /// <summary>一次只发一条请求——套壳内部也是串行处理的。</summary>
    private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

    /// <summary>进程与管道守护锁。</summary>
    private readonly object _gate = new object();

    private Process? _process;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private Task<string?>? _pendingRead;
    private int _nextId;

    /// <summary>以套壳路径与数据根构造。</summary>
    /// <param name="exePath">ComfyDock.Shell.exe 完整路径。</param>
    /// <param name="dataDir">数据根目录。</param>
    public ShellClient(string exePath, string dataDir)
    {
        _exePath = exePath;
        _dataDir = dataDir;
    }

    /// <summary>套壳进程是否在跑。</summary>
    public bool Running
    {
        get
        {
            lock (_gate)
            {
                return _process != null && !_process.HasExited;
            }
        }
    }

    /// <summary>打开某账号的地址并等导航结束。</summary>
    /// <param name="accountId">账号 id。</param>
    /// <param name="url">目标地址。</param>
    /// <param name="visible">是否把套壳窗口搬到前台（false = 后台刷新，不打扰人）。</param>
    /// <param name="timeoutMs">超时毫秒。</param>
    /// <returns>应答结果节点。</returns>
    public Task<JsonElement> OpenAsync(string accountId, string url, bool visible, int timeoutMs)
    {
        Dictionary<string, object?> request = new Dictionary<string, object?>();
        request["op"] = "open";
        request["account"] = accountId;
        request["url"] = url;
        request["visible"] = visible;
        return CallAsync(request, timeoutMs);
    }

    /// <summary>在某账号页面内执行脚本。</summary>
    /// <param name="accountId">账号 id。</param>
    /// <param name="script">脚本正文。</param>
    /// <param name="timeoutMs">超时毫秒。</param>
    /// <returns>应答结果节点。</returns>
    public Task<JsonElement> EvalAsync(string accountId, string script, int timeoutMs)
    {
        Dictionary<string, object?> request = new Dictionary<string, object?>();
        request["op"] = "eval";
        request["account"] = accountId;
        request["script"] = script;
        return CallAsync(request, timeoutMs);
    }

    /// <summary>在某账号页面内轮询执行脚本直到结果非空。</summary>
    /// <param name="accountId">账号 id。</param>
    /// <param name="script">脚本正文。</param>
    /// <param name="timeoutMs">超时毫秒。</param>
    /// <returns>应答结果节点。</returns>
    public Task<JsonElement> WaitAsync(string accountId, string script, int timeoutMs)
    {
        Dictionary<string, object?> request = new Dictionary<string, object?>();
        request["op"] = "wait";
        request["account"] = accountId;
        request["script"] = script;
        request["timeoutMs"] = timeoutMs;
        return CallAsync(request, timeoutMs + 15000);
    }

    /// <summary>让套壳把窗口提到最前。</summary>
    /// <returns>应答结果节点。</returns>
    public Task<JsonElement> ShowAsync()
    {
        Dictionary<string, object?> request = new Dictionary<string, object?>();
        request["op"] = "show";
        return CallAsync(request, 15000);
    }

    /// <summary>读套壳当前账号表。</summary>
    /// <returns>应答结果节点。</returns>
    public Task<JsonElement> ListAsync()
    {
        Dictionary<string, object?> request = new Dictionary<string, object?>();
        request["op"] = "list";
        return CallAsync(request, 15000);
    }

    /// <summary>关闭某账号的视图（登录态留在磁盘）。</summary>
    /// <param name="accountId">账号 id。</param>
    /// <returns>应答结果节点。</returns>
    public Task<JsonElement> CloseAsync(string accountId)
    {
        Dictionary<string, object?> request = new Dictionary<string, object?>();
        request["op"] = "close";
        request["account"] = accountId;
        return CallAsync(request, 30000);
    }

    /// <summary>关闭视图并按需删除该账号的数据目录。</summary>
    /// <param name="accountId">账号 id。</param>
    /// <param name="deleteProfile">是否删除数据目录。</param>
    /// <returns>应答结果节点。</returns>
    public Task<JsonElement> RemoveAsync(string accountId, bool deleteProfile)
    {
        Dictionary<string, object?> request = new Dictionary<string, object?>();
        request["op"] = "remove";
        request["account"] = accountId;
        request["deleteProfile"] = deleteProfile;
        return CallAsync(request, 60000);
    }

    /// <summary>装一个「新文档创建时执行」的脚本（同一账号重复安装自动去重）。</summary>
    /// <param name="accountId">账号 id。</param>
    /// <param name="script">脚本正文。</param>
    /// <returns>应答结果节点。</returns>
    public Task<JsonElement> AddInitScriptAsync(string accountId, string script)
    {
        Dictionary<string, object?> request = new Dictionary<string, object?>();
        request["op"] = "addinit";
        request["account"] = accountId;
        request["script"] = script;
        return CallAsync(request, 90000);
    }

    /// <summary>在账号视图上调用一条 DevTools 协议方法（页面 JS 做不到的动作，如给文件框塞本地文件）。</summary>
    /// <param name="accountId">账号 id。</param>
    /// <param name="method">协议方法名。</param>
    /// <param name="parameters">参数 JSON（空 = 空对象）。</param>
    /// <param name="timeoutMs">超时毫秒。</param>
    /// <returns>应答结果节点。</returns>
    public Task<JsonElement> CallDevToolsAsync(string accountId, string method, string parameters, int timeoutMs)
    {
        Dictionary<string, object?> request = new Dictionary<string, object?>();
        request["op"] = "cdp";
        request["account"] = accountId;
        request["method"] = method;
        request["params"] = parameters;
        return CallAsync(request, timeoutMs);
    }

    /// <summary>调用 DevTools 方法并取回结果 JSON 文本。</summary>
    /// <param name="accountId">账号 id。</param>
    /// <param name="method">协议方法名。</param>
    /// <param name="parameters">参数 JSON。</param>
    /// <param name="timeoutMs">超时毫秒。</param>
    /// <returns>结果 JSON 文本；失败返回空串。</returns>
    public async Task<string> CallDevToolsTextAsync(string accountId, string method, string parameters, int timeoutMs)
    {
        JsonElement root = await CallDevToolsAsync(accountId, method, parameters, timeoutMs).ConfigureAwait(false);
        if (!IsOk(root))
        {
            return "";
        }

        JsonElement result;
        if (!root.TryGetProperty("result", out result) || result.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        JsonElement value;
        if (!result.TryGetProperty("result", out value) || value.ValueKind != JsonValueKind.String)
        {
            return "";
        }

        return value.GetString() ?? "";
    }

    /// <summary>在某账号页面内执行脚本并把结果当作文本返回（JS 值 → JSON → 文本）。</summary>
    /// <param name="accountId">账号 id。</param>
    /// <param name="script">脚本正文。</param>
    /// <param name="timeoutMs">超时毫秒。</param>
    /// <returns>结果文本；失败或空结果返回空串。</returns>
    public async Task<string> EvalTextAsync(string accountId, string script, int timeoutMs)
    {
        JsonElement root = await EvalAsync(accountId, script, timeoutMs).ConfigureAwait(false);
        if (!IsOk(root))
        {
            return "";
        }

        JsonElement result;
        if (!root.TryGetProperty("result", out result) || result.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        JsonElement value;
        if (!result.TryGetProperty("value", out value) || value.ValueKind != JsonValueKind.String)
        {
            return "";
        }

        return DecodeScriptValue(value.GetString() ?? "");
    }

    /// <summary>应答是否成功。</summary>
    /// <param name="root">应答根节点。</param>
    /// <returns>是否成功。</returns>
    public static bool IsOk(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        JsonElement ok;
        if (!root.TryGetProperty("ok", out ok))
        {
            return false;
        }

        return ok.ValueKind == JsonValueKind.True;
    }

    /// <summary>应答失败时的错误文本。</summary>
    /// <param name="root">应答根节点。</param>
    /// <returns>错误文本；成功返回空串。</returns>
    public static string ErrorText(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return "套壳应答格式异常";
        }

        JsonElement error;
        if (!root.TryGetProperty("error", out error))
        {
            return "";
        }

        if (error.ValueKind != JsonValueKind.String)
        {
            return "";
        }

        return error.GetString() ?? "";
    }

    private static string DecodeScriptValue(string raw)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind == JsonValueKind.String)
            {
                return document.RootElement.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
            return raw;
        }

        return raw;
    }

    /// <summary>发一条请求并等应答。</summary>
    /// <param name="request">请求体（op 必填）。</param>
    /// <param name="timeoutMs">超时毫秒。</param>
    /// <returns>应答根节点（result 或 error 在其中）。</returns>
    public async Task<JsonElement> CallAsync(Dictionary<string, object?> request, int timeoutMs)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureStarted();
            int id = NextId();
            request["id"] = id;
            string line = JsonSerializer.Serialize(request, RequestOptions);

            StreamWriter writer = _writer!;
            await writer.WriteLineAsync(line).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);

            while (true)
            {
                Task<string?> reading = ReadLineSharedAsync();
                Task finished = await Task.WhenAny(reading, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (finished != reading)
                {
                    // 超时不丢挂起的读——下一次请求继续等同一个读任务，避免「流被上一次操作占用」
                    throw new TimeoutException("套壳无应答（超过 " + timeoutMs + " 毫秒）：" + request["op"]);
                }

                string? response = await reading.ConfigureAwait(false);
                _pendingRead = null;
                if (response == null)
                {
                    Stop();
                    throw new InvalidOperationException("套壳已退出：收到空应答");
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(response);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (document)
                {
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (root.TryGetProperty("ev", out _))
                    {
                        continue;
                    }

                    if (!root.TryGetProperty("id", out JsonElement idElement) || !idElement.TryGetInt32(out int responseId))
                    {
                        continue;
                    }

                    if (responseId != id)
                    {
                        continue;
                    }

                    return root.Clone();
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>释放套壳——关标准输入，套壳读到 EOF 自行退场。</summary>
    /// <returns>异步任务。</returns>
    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }

    private int NextId()
    {
        _nextId = _nextId + 1;
        return _nextId;
    }

    /// <summary>共享同一个挂起读任务——超时后不重新发起读取，避免标准输出流被两个读操作争用。</summary>
    /// <returns>读任务（可能尚未完成）。</returns>
    private Task<string?> ReadLineSharedAsync()
    {
        if (_pendingRead == null)
        {
            _pendingRead = _reader!.ReadLineAsync();
        }

        return _pendingRead;
    }

    // [段1] 进程守护
    private void EnsureStarted()
    {
        lock (_gate)
        {
            if (_process != null && !_process.HasExited)
            {
                return;
            }

            if (!File.Exists(_exePath))
            {
                throw new FileNotFoundException("套壳程序不存在：" + _exePath);
            }

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = _exePath;
            psi.WorkingDirectory = Path.GetDirectoryName(_exePath) ?? _dataDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardInputEncoding = new UTF8Encoding(false);
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            psi.ArgumentList.Add("--data");
            psi.ArgumentList.Add(_dataDir);
            psi.ArgumentList.Add("--bridge");

            Process? process = Process.Start(psi);
            if (process == null)
            {
                throw new InvalidOperationException("套壳启动失败：" + _exePath);
            }

            _process = process;
            _writer = process.StandardInput;
            _writer.AutoFlush = true;
            _reader = process.StandardOutput;
            DrainErrorAsync(process);
        }
    }

    private static async void DrainErrorAsync(Process process)
    {
        try
        {
            while (true)
            {
                string? line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                {
                    return;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Stop()
    {
        lock (_gate)
        {
            if (_process == null)
            {
                return;
            }

            try
            {
                if (_writer != null && !_process.HasExited)
                {
                    _writer.Close();
                }
            }
            catch (IOException)
            {
            }

            try
            {
                if (!_process.WaitForExit(3000))
                {
                    _process.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            _process.Dispose();
            _process = null;
            _writer = null;
            _reader = null;
            _pendingRead = null;
        }
    }
}
