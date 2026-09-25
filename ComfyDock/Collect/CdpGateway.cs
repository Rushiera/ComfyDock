using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Collect;

/// <summary>该账号的浏览器由面板之外打开（没有调试端口）——面板控制不到，需要先「接管」。</summary>
public sealed class BrowserBusyException : Exception
{
    /// <summary>以说明文本构造。</summary>
    /// <param name="message">说明文本。</param>
    public BrowserBusyException(string message)
        : base(message)
    {
    }
}

/// <summary>CDP 会话入口——统一决定「这次操作用哪个浏览器执行」：
/// 有受控实例就复用它（面板打开的窗口，走通道标签页）→ 占用者是面板自有残留 headless 则回收后继续
/// → 占用者是面板之外的可见窗口则报「需接管」→ 否则起 headless 临时实例（采完即杀）。
/// 占用判定以进程命令行为准——锁文件看不见 headless 实例，只剩兜底作用。</summary>
public sealed class CdpGateway
{
    /// <summary>chrome.exe 路径。</summary>
    private readonly string _chromePath;
    /// <summary>面板之外的实例占着用户目录时的统一提示（进程扫描与锁文件兜底共用）。</summary>
    private const string BusyMessage = "该账号的浏览器已打开，且不是面板打开的（面板控制不到）——点「接管」重开后即可执行";

    /// <summary>受控浏览器中心。</summary>
    private readonly BrowserHub _hub;

    /// <summary>构造网关。</summary>
    /// <param name="chromePath">chrome.exe 路径。</param>
    /// <param name="hub">受控浏览器中心。</param>
    public CdpGateway(string chromePath, BrowserHub hub)
    {
        _chromePath = chromePath;
        _hub = hub;
    }

    /// <summary>取一个 CDP 会话（受控实例优先，其次 headless 临时实例）。</summary>
    /// <param name="profileDir">浏览器用户数据目录。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>会话租约。</returns>
    public async Task<CdpLease> AcquireAsync(string profileDir, CancellationToken ct)
    {
        // [段1] 该账号有受控实例 → 复用，不新起窗口
        BrowserInstance? instance = await _hub.FindAliveAsync(profileDir).ConfigureAwait(false);
        if (instance is not null)
        {
            string socket = await _hub.EnsureChannelSocketAsync(instance, ct).ConfigureAwait(false);
            CdpSession reused = await CdpSession.ConnectAsync(socket, 20000, ct).ConfigureAwait(false);
            return new CdpLease(reused);
        }

        // [段2] 占用判定以进程为准（锁文件看不见 headless 实例）——全为面板自有残留则回收后继续
        List<ProfileOccupant> occupants = await _hub.FindOccupantsAsync(profileDir, ct).ConfigureAwait(false);
        if (occupants.Count > 0)
        {
            if (!AllHeadless(occupants))
            {
                throw new BrowserBusyException(BusyMessage);
            }

            await _hub.KillByProfileAsync(profileDir, ct).ConfigureAwait(false);
        }

        // [段3] 进程扫描为空、锁文件却指向活进程——保守处理，不硬闯
        if (occupants.Count == 0 && IsProfileLocked(profileDir))
        {
            throw new BrowserBusyException(BusyMessage);
        }

        // [段4] 起 headless 临时实例
        return await AcquireTemporaryAsync(profileDir, ct).ConfigureAwait(false);
    }

    /// <summary>强制使用 headless 临时实例（诊断入口用——不受受控实例影响）。</summary>
    /// <param name="profileDir">浏览器用户数据目录。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>会话租约。</returns>
    public async Task<CdpLease> AcquireTemporaryAsync(string profileDir, CancellationToken ct)
    {
        CdpSession session = await CdpSession.StartAsync(_chromePath, profileDir, 30000, ct).ConfigureAwait(false);
        return new CdpLease(session);
    }
    /// <summary>判断占用者是否全为 headless 实例（面板的采集临时实例——残留则可回收）。</summary>
    /// <param name="occupants">占用者清单。</param>
    /// <returns>是否全为 headless。</returns>
    private static bool AllHeadless(List<ProfileOccupant> occupants)
    {
        foreach (ProfileOccupant occupant in occupants)
        {
            if (!occupant.Headless)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>判断用户目录是否被面板之外的实例占用（解析单例锁指向的进程是否还活着）。</summary>
    /// <param name="profileDir">浏览器用户数据目录。</param>
    /// <returns>是否被占用。</returns>
    private static bool IsProfileLocked(string profileDir)
    {
        int pid = ReadSingletonPid(Path.Combine(profileDir, "SingletonLock"));
        if (pid <= 0)
        {
            pid = ReadSingletonPid(Path.Combine(profileDir, "lockfile"));
        }

        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>从单例锁读出占用进程 id（符号链接名 / 文件内容形如「主机名-进程号」）。</summary>
    /// <param name="lockPath">锁文件路径。</param>
    /// <returns>进程 id；读不出返回 0。</returns>
    private static int ReadSingletonPid(string lockPath)
    {
        string target = "";

        // [段1] 符号链接形态（chrome 现行做法——链接目标通常不存在，故不能用 File.Exists 判在）
        try
        {
            FileSystemInfo? link = File.ResolveLinkTarget(lockPath, false);
            if (link is not null)
            {
                target = link.Name;
            }
        }
        catch (FileNotFoundException)
        {
            return 0;
        }
        catch (DirectoryNotFoundException)
        {
            return 0;
        }
        catch (IOException)
        {
            // 不是符号链接——退化为读文本
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }

        // [段2] 文本形态（旧版 chrome 把「主机名-进程号」写进文件）
        if (target.Length == 0)
        {
            try
            {
                if (!File.Exists(lockPath))
                {
                    return 0;
                }

                target = File.ReadAllText(lockPath).Trim();
            }
            catch (IOException)
            {
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                return 0;
            }
        }

        int dash = target.LastIndexOf('-');
        if (dash < 0 || dash + 1 >= target.Length)
        {
            return 0;
        }

        if (int.TryParse(target.Substring(dash + 1), out int pid))
        {
            return pid;
        }

        return 0;
    }
}

/// <summary>
/// CDP 会话租约——会话自己知道是否持有浏览器进程：自有进程（headless 临时实例）在 Dispose 时结束，
/// 外部实例（受控浏览器）继续存活供后续复用。
/// </summary>
public sealed class CdpLease : IAsyncDisposable
{
    /// <summary>构造租约。</summary>
    /// <param name="session">CDP 会话。</param>
    public CdpLease(CdpSession session)
    {
        Session = session;
    }

    /// <summary>CDP 会话。</summary>
    public CdpSession Session { get; }

    /// <summary>释放会话。</summary>
    /// <returns>异步任务。</returns>
    public ValueTask DisposeAsync()
    {
        return Session.DisposeAsync();
    }
}
