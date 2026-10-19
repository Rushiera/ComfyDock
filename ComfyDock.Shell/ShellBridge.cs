using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Shell;

/// <summary>
/// 桥接层——与主程序的标准输入输出行协议：一行一条请求，一行一条应答。
/// 请求 <c>{"id":1,"op":"open","account":"acc-1","url":"..."}</c>；
/// 应答 <c>{"id":1,"ok":true,"result":{...}}</c> 或 <c>{"id":1,"ok":false,"error":"..."}</c>。
/// 不占端口——两个进程靠管道说话。
/// </summary>
public sealed class ShellBridge
{
    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly object LogLock = new object();

    private readonly ShellForm _form;
    private readonly ShellOptions _options;

    private TextReader _reader = null!;
    private StreamWriter _writer = null!;

    /// <summary>以窗口与选项构造。</summary>
    /// <param name="form">套壳主窗口。</param>
    /// <param name="options">套壳选项。</param>
    public ShellBridge(ShellForm form, ShellOptions options)
    {
        _form = form;
        _options = options;
    }

    /// <summary>起读线线程并广播 ready 事件。</summary>
    public void Start()
    {
        _reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        _writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
        _writer.AutoFlush = true;

        Thread thread = new Thread(Loop);
        thread.IsBackground = true;
        thread.Name = "shell-bridge";
        thread.Start();

        Dictionary<string, object?> ready = new Dictionary<string, object?>();
        ready["ev"] = "ready";
        ready["pid"] = Environment.ProcessId;
        ready["dataDir"] = _options.DataDir;
        ready["accounts"] = _form.Accounts.Count;
        WritePayload(ready);
    }

    // [段1] 读线
    private void Loop()
    {
        while (true)
        {
            string? line;
            try
            {
                line = _reader.ReadLine();
            }
            catch (IOException ex)
            {
                Log("读标准输入失败：" + ex.Message);
                break;
            }

            if (line == null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            HandleLine(line);
        }

        Log("标准输入关闭——套壳随主程序退出");
        try
        {
            _form.BeginInvoke(new Action(CloseQuietly));
        }
        catch (Exception ex)
        {
            Log("关闭窗口失败：" + ex.Message);
        }
    }

    private void CloseQuietly()
    {
        _form.Close();
    }

    // [段2] 单条请求
    private void HandleLine(string line)
    {
        JsonDocument? document = null;
        int id = 0;
        string error = "";
        object? result = null;

        try
        {
            document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            id = GetInt(root, "id", 0);
            string op = GetString(root, "op");
            Task<object> work = DispatchAsync(op, root);
            result = work.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            Log("请求失败：" + line + " → " + error);
        }
        finally
        {
            if (document != null)
            {
                document.Dispose();
            }
        }

        Dictionary<string, object?> payload = new Dictionary<string, object?>();
        payload["id"] = id;
        payload["ok"] = error.Length == 0;
        if (error.Length == 0)
        {
            payload["result"] = result;
        }
        else
        {
            payload["error"] = error;
        }

        WritePayload(payload);
    }

    // [段3] 派发到界面线程
    private Task<object> DispatchAsync(string op, JsonElement root)
    {
        TaskCompletionSource<object> completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        _form.BeginInvoke(new Action(async delegate
        {
            try
            {
                object value = await HandleAsync(op, root);
                completion.TrySetResult(value);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }));

        return completion.Task;
    }

    private async Task<object> HandleAsync(string op, JsonElement root)
    {
        if (string.Equals(op, "ping", StringComparison.Ordinal))
        {
            return new
            {
                pong = true,
                pid = Environment.ProcessId,
                accounts = _form.Accounts.Count,
                opened = _form.OpenedAccountIds(),
            };
        }

        if (string.Equals(op, "list", StringComparison.Ordinal))
        {
            return new { accounts = BuildAccountList() };
        }

        if (string.Equals(op, "reload", StringComparison.Ordinal))
        {
            _form.ReloadRegistry();
            return new { accounts = BuildAccountList() };
        }

        if (string.Equals(op, "show", StringComparison.Ordinal))
        {
            _form.BringToFrontSafe();
            return new { shown = true };
        }

        if (string.Equals(op, "open", StringComparison.Ordinal))
        {
            return await _form.OpenAsync(GetString(root, "account"), GetString(root, "url"), GetBool(root, "visible", true));
        }

        if (string.Equals(op, "eval", StringComparison.Ordinal))
        {
            return await _form.EvalAsync(GetString(root, "account"), GetString(root, "script"));
        }

        if (string.Equals(op, "addinit", StringComparison.Ordinal))
        {
            return await _form.AddInitScriptAsync(GetString(root, "account"), GetString(root, "script"));
        }

        if (string.Equals(op, "wait", StringComparison.Ordinal))
        {
            return await _form.WaitAsync(GetString(root, "account"), GetString(root, "script"), GetInt(root, "timeoutMs", 0));
        }

        if (string.Equals(op, "close", StringComparison.Ordinal))
        {
            return _form.Close(GetString(root, "account"));
        }

        if (string.Equals(op, "remove", StringComparison.Ordinal))
        {
            return await _form.RemoveAsync(GetString(root, "account"), GetBool(root, "deleteProfile"));
        }

        if (string.Equals(op, "cdp", StringComparison.Ordinal))
        {
            return await _form.CallDevToolsAsync(GetString(root, "account"), GetString(root, "method"), GetString(root, "params"));
        }

        throw new InvalidOperationException("未知操作：" + op);
    }

    private List<object> BuildAccountList()
    {
        List<object> list = new List<object>();
        foreach (ShellAccount account in _form.Accounts)
        {
            list.Add(new
            {
                id = account.Id,
                name = account.Name,
                profileDir = account.ProfileDir,
                enabled = account.Enabled,
                note = account.Note,
            });
        }

        return list;
    }

    // [段4] 取值与输出
    private static string GetString(JsonElement root, string name)
    {
        JsonElement value;
        if (!root.TryGetProperty(name, out value))
        {
            return "";
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return "";
        }

        return value.GetString() ?? "";
    }

    private static int GetInt(JsonElement root, string name, int fallback)
    {
        JsonElement value;
        if (!root.TryGetProperty(name, out value))
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.Number)
        {
            return fallback;
        }

        int parsed;
        if (!value.TryGetInt32(out parsed))
        {
            return fallback;
        }

        return parsed;
    }

    private static bool GetBool(JsonElement root, string name)
    {
        return GetBool(root, name, false);
    }

    private static bool GetBool(JsonElement root, string name, bool fallback)
    {
        JsonElement value;
        if (!root.TryGetProperty(name, out value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        return fallback;
    }

    private void WritePayload(Dictionary<string, object?> payload)
    {
        try
        {
            _writer.WriteLine(JsonSerializer.Serialize(payload, SerializerOptions));
        }
        catch (Exception ex)
        {
            Log("写标准输出失败：" + ex.Message);
        }
    }

    private void Log(string message)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(_options.LogDir);
                string path = Path.Combine(_options.LogDir, "shell.log");
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
            }
        }
        catch (IOException)
        {
        }
    }
}
