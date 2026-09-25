using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Collect;

/// <summary>
/// 受控浏览器中心——登记「由面板启动、保留调试端口的浏览器实例」（用户目录 → 端口 / 进程），
/// 负责复用探活、按需启动、取通道标签页与「接管」（结束非受控实例）。
/// 面板由此对自己打开的窗口保有控制权：即使某个工作流页面正开着，删除 / 上传 / 采集也能执行。
/// </summary>
public sealed class BrowserHub
{
    /// <summary>登记表序列化选项。</summary>
    private static readonly JsonSerializerOptions RegistryOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>调试端点请求用的共享客户端。</summary>
    private static readonly HttpClient Http = new HttpClient();

    /// <summary>chrome.exe 路径。</summary>
    private readonly string _chromePath;

    /// <summary>登记表路径（data/browsers.json）。</summary>
    private readonly string _registryPath;

    /// <summary>登记表读写锁。</summary>
    private readonly object _gate = new object();

    /// <summary>受控实例表（用户目录 → 实例）。</summary>
    private readonly Dictionary<string, BrowserInstance> _instances =
        new Dictionary<string, BrowserInstance>(StringComparer.OrdinalIgnoreCase);

    /// <summary>启动互斥——避免同一账号并发起两个窗口。</summary>
    private readonly SemaphoreSlim _launchLock = new SemaphoreSlim(1, 1);

    /// <summary>构造受控浏览器中心（启动即读登记表）。</summary>
    /// <param name="chromePath">chrome.exe 路径。</param>
    /// <param name="registryPath">登记表路径（data/browsers.json）。</param>
    public BrowserHub(string chromePath, string registryPath)
    {
        _chromePath = chromePath;
        _registryPath = registryPath;
        LoadRegistry();
    }

    /// <summary>登记表路径。</summary>
    public string RegistryPath => _registryPath;

    /// <summary>取该账号的存活受控实例（不启动）。</summary>
    /// <param name="profileDir">浏览器用户数据目录。</param>
    /// <returns>存活实例；无则 null。</returns>
    public async Task<BrowserInstance?> FindAliveAsync(string profileDir)
    {
        BrowserInstance? known = Get(profileDir);
        if (known is null)
        {
            return null;
        }

        if (await IsAliveAsync(known.Port, CancellationToken.None).ConfigureAwait(false))
        {
            return known;
        }

        Remove(profileDir);
        return null;
    }
    /// <summary>列出该用户目录下正在运行的 chrome 主进程（不探活、不结束）——占用判定的权威来源。</summary>
    /// <param name="profileDir">浏览器用户数据目录。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>占用者清单（空表 = 无人占用）。</returns>
    public async Task<List<ProfileOccupant>> FindOccupantsAsync(string profileDir, CancellationToken ct)
    {
        List<ProfileOccupant> procs = await QueryChromeMainsAsync(ct).ConfigureAwait(false);
        List<ProfileOccupant> result = new List<ProfileOccupant>();

        foreach (ProfileOccupant proc in procs)
        {
            if (!string.Equals(ReadProfileDir(proc.CommandLine), profileDir, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(proc);
        }

        return result;
    }
    /// <summary>回收残留 headless 实例——上一代面板被接手 / 退出时留下的采集临时进程（父进程已死，仍占着用户目录）。</summary>
    /// <param name="profileRoot">账号用户目录的父目录（快捷方式扫描目录）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>回收的进程数。</returns>
    public async Task<int> SweepResidualHeadlessAsync(string profileRoot, CancellationToken ct)
    {
        List<ProfileOccupant> procs = await QueryChromeMainsAsync(ct).ConfigureAwait(false);
        int killed = 0;

        foreach (ProfileOccupant proc in procs)
        {
            if (!IsHeadlessUnderRoot(proc.CommandLine, profileRoot))
            {
                continue;
            }

            if (TryKillTree(proc.Pid))
            {
                killed = killed + 1;
            }
        }

        return killed;
    }

    /// <summary>确保该账号有一个受控实例——活着就复用，否则启动可见窗口。</summary>
    /// <param name="profileDir">浏览器用户数据目录。</param>
    /// <param name="url">启动时打开的地址（复用已有实例时不导航）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受控实例 + 本次是否新建。</returns>
    public async Task<BrowserOpenResult> EnsureAsync(string profileDir, string url, CancellationToken ct)
    {
        BrowserInstance? alive = await FindAliveAsync(profileDir).ConfigureAwait(false);
        if (alive is not null)
        {
            return new BrowserOpenResult { Instance = alive, Launched = false };
        }

        await _launchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // [段1] 双检——等锁期间可能已由另一请求启动
            alive = await FindAliveAsync(profileDir).ConfigureAwait(false);
            if (alive is not null)
            {
                return new BrowserOpenResult { Instance = alive, Launched = false };
            }

            // [段2] 起受控窗口并登记
            ChromeProcess chrome = await ChromeLauncher.StartWindowAsync(_chromePath, profileDir, url, 30000, ct).ConfigureAwait(false);
            var instance = new BrowserInstance
            {
                ProfileDir = profileDir,
                Port = chrome.Port,
                Pid = chrome.Pid,
                StartedAt = Clock.Now(),
                ChannelTargetId = "",
            };

            lock (_gate)
            {
                _instances[profileDir] = instance;
                SaveRegistry();
            }

            return new BrowserOpenResult { Instance = instance, Launched = true };
        }
        finally
        {
            _launchLock.Release();
        }
    }

    /// <summary>在受控实例里打开一个前台标签页（用户点了「打开」要看得见）。</summary>
    /// <param name="instance">受控实例。</param>
    /// <param name="url">目标地址。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public async Task OpenTabAsync(BrowserInstance instance, string url, CancellationToken ct)
    {
        TargetInfo? target = await CreateTargetAsync(instance.Port, url, ct).ConfigureAwait(false);
        if (target is null)
        {
            throw new InvalidOperationException("受控浏览器未接受新标签页请求（端口 " + instance.Port + "）");
        }
    }

    /// <summary>取该实例的通道标签页页面通道——不存在则新建一个空白页并登记。</summary>
    /// <param name="instance">受控实例。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>页面级调试端点（ws://…）。</returns>
    public async Task<string> EnsureChannelSocketAsync(BrowserInstance instance, CancellationToken ct)
    {
        // [段1] 登记的通道标签页仍活着 → 直接复用
        if (instance.ChannelTargetId.Length > 0)
        {
            string? found = await FindTargetSocketAsync(instance.Port, instance.ChannelTargetId, ct).ConfigureAwait(false);
            if (found is not null)
            {
                return found;
            }
        }

        // [段2] 新建通道标签页并登记
        TargetInfo? created = await CreateTargetAsync(instance.Port, "about:blank", ct).ConfigureAwait(false);
        if (created is null)
        {
            throw new InvalidOperationException("受控浏览器未接受新标签页请求（端口 " + instance.Port + "）");
        }

        instance.ChannelTargetId = created.Id;
        lock (_gate)
        {
            SaveRegistry();
        }

        return created.SocketUrl;
    }

    /// <summary>结束该用户目录下的 chrome 主进程（含子进程）——两个入口共用：用户点「接管」，或采集前判定为面板自有残留 headless。</summary>
    /// <param name="profileDir">浏览器用户数据目录。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>结束的进程数。</returns>
    public async Task<int> KillByProfileAsync(string profileDir, CancellationToken ct)
    {
        List<ProfileOccupant> occupants = await FindOccupantsAsync(profileDir, ct).ConfigureAwait(false);
        int killed = 0;

        // [段1] 逐个结束（含子进程）——只在用户点「接管」或判定为残留时走到这里
        foreach (ProfileOccupant occupant in occupants)
        {
            if (TryKillTree(occupant.Pid))
            {
                killed = killed + 1;
            }
        }

        // [段2] 等锁文件释放——避免紧接着启动时被陈旧单例锁挡住
        await Task.Delay(1200, ct).ConfigureAwait(false);

        lock (_gate)
        {
            _instances.Remove(profileDir);
            SaveRegistry();
        }

        return killed;
    }

    /// <summary>探测端口上的浏览器是否还在。</summary>
    /// <param name="port">调试端口。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>是否存活。</returns>
    public static async Task<bool> IsAliveAsync(int port, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(1500);

        try
        {
            string text = await Http.GetStringAsync("http://127.0.0.1:" + port + "/json/version", cts.Token).ConfigureAwait(false);
            return text.Length > 0;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>查登记表里的实例（不探活）。</summary>
    private BrowserInstance? Get(string profileDir)
    {
        lock (_gate)
        {
            if (_instances.TryGetValue(profileDir, out BrowserInstance? found))
            {
                return found;
            }

            return null;
        }
    }

    /// <summary>从登记表移除一个实例并落盘。</summary>
    private void Remove(string profileDir)
    {
        lock (_gate)
        {
            if (_instances.Remove(profileDir))
            {
                SaveRegistry();
            }
        }
    }

    /// <summary>按 target id 找页面级调试端点。</summary>
    /// <param name="port">调试端口。</param>
    /// <param name="targetId">标签页 id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>页面级端点；未找到返回 null。</returns>
    private async Task<string?> FindTargetSocketAsync(int port, string targetId, CancellationToken ct)
    {
        string? body = await SendAsync(HttpMethod.Get, "http://127.0.0.1:" + port + "/json/list", ct).ConfigureAwait(false);
        if (body is null)
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out JsonElement id) || id.GetString() != targetId)
                {
                    continue;
                }

                if (item.TryGetProperty("webSocketDebuggerUrl", out JsonElement socket))
                {
                    string url = socket.GetString() ?? "";
                    return url.Length > 0 ? url : null;
                }
            }
        }
        catch (JsonException)
        {
            // 列表不可解析——按未找到处理。
        }

        return null;
    }

    /// <summary>让浏览器开一个新标签页。</summary>
    /// <param name="port">调试端口。</param>
    /// <param name="url">标签页地址。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>标签页信息；失败返回 null。</returns>
    private static async Task<TargetInfo?> CreateTargetAsync(int port, string url, CancellationToken ct)
    {
        string path = "http://127.0.0.1:" + port + "/json/new?url=" + Uri.EscapeDataString(url);

        // [段1] 新版 chrome 只认 PUT——失败退回 GET（旧版）
        string? body = await SendAsync(HttpMethod.Put, path, ct).ConfigureAwait(false);
        if (body is null)
        {
            body = await SendAsync(HttpMethod.Get, path, ct).ConfigureAwait(false);
        }

        if (body is null)
        {
            return null;
        }

        return ParseTarget(body);
    }

    /// <summary>解析新建标签页的返回（对象或数组）。</summary>
    /// <param name="body">端点返回体。</param>
    /// <returns>标签页信息；不可解析返回 null。</returns>
    private static TargetInfo? ParseTarget(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;

            // [段1] 端点可能回对象（单条）或数组（多条）
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0)
                {
                    return null;
                }

                root = root[0];
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string id = root.TryGetProperty("id", out JsonElement idElement) ? (idElement.GetString() ?? "") : "";
            string socket = root.TryGetProperty("webSocketDebuggerUrl", out JsonElement socketElement) ? (socketElement.GetString() ?? "") : "";

            if (id.Length == 0 || socket.Length == 0)
            {
                return null;
            }

            return new TargetInfo { Id = id, SocketUrl = socket };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>向调试端点发一个请求并取回文本。</summary>
    /// <param name="method">HTTP 方法。</param>
    /// <param name="url">请求地址。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>响应文本；失败返回 null。</returns>
    private static async Task<string?> SendAsync(HttpMethod method, string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(10000);

        try
        {
            using var request = new HttpRequestMessage(method, url);
            using HttpResponseMessage response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
    /// <summary>从一条 chrome 进程记录里挑出主进程（跳过渲染 / 辅助子进程）并收集。</summary>
    /// <param name="item">CIM 返回的单条记录。</param>
    /// <param name="target">命中的进程信息收集处。</param>
    private static void CollectProc(JsonElement item, List<ProfileOccupant> target)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!item.TryGetProperty("ProcessId", out JsonElement idElement) || !idElement.TryGetInt32(out int pid))
        {
            return;
        }

        string commandLine = item.TryGetProperty("CommandLine", out JsonElement line) ? (line.GetString() ?? "") : "";
        if (commandLine.Length == 0)
        {
            return;
        }

        if (commandLine.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return;
        }

        target.Add(new ProfileOccupant
        {
            Pid = pid,
            CommandLine = commandLine,
            Headless = commandLine.IndexOf("--headless", StringComparison.OrdinalIgnoreCase) >= 0,
        });
    }
    /// <summary>借 PowerShell 查全部 chrome 主进程（id + 命令行）——占用判定与残留回收共用一次查询。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>主进程信息列表；查询失败返回空表。</returns>
    private static async Task<List<ProfileOccupant>> QueryChromeMainsAsync(CancellationToken ct)
    {
        List<ProfileOccupant> result = new List<ProfileOccupant>();
        string command = "Get-CimInstance Win32_Process -Filter \"Name='chrome.exe'\" | Select-Object ProcessId,CommandLine | ConvertTo-Json -Compress";

        // [段1] 借 PowerShell 取进程命令行——.NET 无零依赖的跨进程命令行查询
        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);

        using Process? shell = Process.Start(psi);
        if (shell is null)
        {
            return result;
        }

        string output = await shell.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await shell.WaitForExitAsync(ct).ConfigureAwait(false);

        if (output.Trim().Length == 0)
        {
            return result;
        }

        // [段2] 逐条解析——CIM 的 JSON 可能回对象（单条）或数组（多条）
        try
        {
            using JsonDocument doc = JsonDocument.Parse(output);
            JsonElement root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                CollectProc(root, result);
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in root.EnumerateArray())
                {
                    CollectProc(item, result);
                }
            }
        }
        catch (JsonException)
        {
            // 输出不可解析——按未找到处理。
        }

        return result;
    }
    /// <summary>从命令行里读出 --user-data-dir 的值（含带引号形态）。</summary>
    /// <param name="commandLine">进程命令行。</param>
    /// <returns>用户目录（未去尾部斜杠）；读不出返回空串。</returns>
    private static string ReadProfileDir(string commandLine)
    {
        const string marker = "--user-data-dir=";
        int at = commandLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return "";
        }

        string rest = commandLine.Substring(at + marker.Length);

        // [段1] 子进程带引号形态（"…\5"）——按引号闭合截取
        if (rest.StartsWith("\"", StringComparison.Ordinal))
        {
            int end = rest.IndexOf('"', 1);
            if (end < 0)
            {
                return "";
            }

            return rest.Substring(1, end - 1).Trim();
        }

        // [段2] 主进程无引号形态——截到空格
        int space = rest.IndexOf(' ');
        if (space < 0)
        {
            return rest.Trim();
        }

        return rest.Substring(0, space).Trim();
    }
    /// <summary>判断一条 chrome 主进程是否为该目录下应当回收的 headless 残留。</summary>
    /// <param name="commandLine">进程命令行。</param>
    /// <param name="profileRoot">账号用户目录的父目录（快捷方式扫描目录）。</param>
    /// <returns>是否命中。</returns>
    private static bool IsHeadlessUnderRoot(string commandLine, string profileRoot)
    {
        if (commandLine.IndexOf("--headless", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        string dir = ReadProfileDir(commandLine);
        if (dir.Length == 0)
        {
            return false;
        }

        string root = profileRoot.TrimEnd('\\');
        if (!dir.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // [段1] 前缀判断必须带分隔符边界——避免 google 误伤 google-old
        if (dir.Length == root.Length)
        {
            return false;
        }

        return dir[root.Length] == '\\';
    }
    /// <summary>结束一个进程及其子进程（清理用，失败不抛）。</summary>
    /// <param name="pid">进程 id。</param>
    /// <returns>是否成功结束。</returns>
    private static bool TryKillTree(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (ArgumentException)
        {
            // 进程已退出。
            return false;
        }
        catch (InvalidOperationException)
        {
            // 进程状态不可用。
            return false;
        }
        catch (Win32Exception)
        {
            // 无权结束。
            return false;
        }
    }

    /// <summary>读登记表（文件缺失或损坏按空表处理）。</summary>
    private void LoadRegistry()
    {
        if (!File.Exists(_registryPath))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(_registryPath);
            BrowserRegistry? registry = JsonSerializer.Deserialize<BrowserRegistry>(json, RegistryOptions);
            if (registry is null || registry.Instances is null)
            {
                return;
            }

            foreach (BrowserInstance instance in registry.Instances)
            {
                if (instance.ProfileDir.Length > 0 && instance.Port > 0)
                {
                    _instances[instance.ProfileDir] = instance;
                }
            }
        }
        catch (JsonException)
        {
            // 登记表损坏——按空表处理（实例仍可用，只是失去复用凭据）。
        }
    }

    /// <summary>写登记表（父目录不存在则创建）。</summary>
    private void SaveRegistry()
    {
        var registry = new BrowserRegistry();
        foreach (KeyValuePair<string, BrowserInstance> pair in _instances)
        {
            registry.Instances.Add(pair.Value);
        }

        string? dir = Path.GetDirectoryName(_registryPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(_registryPath, JsonSerializer.Serialize(registry, RegistryOptions));
    }

    /// <summary>标签页信息（新建标签页时回读）。</summary>
    private sealed class TargetInfo
    {
        /// <summary>标签页 target id。</summary>
        public string Id { get; set; } = "";

        /// <summary>页面级调试端点（ws://…）。</summary>
        public string SocketUrl { get; set; } = "";
    }
}

/// <summary>一个占用用户目录的 chrome 主进程（pid + 命令行）——占用判定与残留回收共用。</summary>
public sealed class ProfileOccupant
{
    /// <summary>进程 id。</summary>
    public int Pid { get; set; }

    /// <summary>进程命令行（占用判定与残留识别都读它）。</summary>
    public string CommandLine { get; set; } = "";

    /// <summary>是否 headless 实例（面板的采集临时实例——采集结束本该回收，留下即为残留）。</summary>
    public bool Headless { get; set; }
}

/// <summary>受控浏览器实例登记项。</summary>
public sealed class BrowserInstance
{
    /// <summary>浏览器用户数据目录。</summary>
    public string ProfileDir { get; set; } = "";

    /// <summary>调试端口。</summary>
    public int Port { get; set; }

    /// <summary>主进程 id（软件重启后仍可用于定位）。</summary>
    public int Pid { get; set; }

    /// <summary>启动时刻（yyyy-MM-dd HH:mm:ss）。</summary>
    public string StartedAt { get; set; } = "";

    /// <summary>面板通道标签页的 target id（空 = 尚未建立）。</summary>
    public string ChannelTargetId { get; set; } = "";
}

/// <summary>登记表落盘结构（data/browsers.json）。</summary>
public sealed class BrowserRegistry
{
    /// <summary>受控实例清单。</summary>
    public List<BrowserInstance> Instances { get; set; } = new List<BrowserInstance>();
}

/// <summary>受控打开结果——实例 + 本次是否新建（新建时已打开目标地址，复用时才需要另开标签页）。</summary>
public sealed class BrowserOpenResult
{
    /// <summary>受控实例。</summary>
    public BrowserInstance Instance { get; set; } = new BrowserInstance();

    /// <summary>本次是否新建了浏览器窗口。</summary>
    public bool Launched { get; set; }
}
