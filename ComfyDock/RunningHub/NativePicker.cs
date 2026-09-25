using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.RunningHub;

/// <summary>
/// 系统文件 / 目录选择对话框——面板「选择音频」「选择目录」两条通道的落点。
/// 走 Win32 通用对话框（comdlg32 / shell32），零第三方依赖；对话框在独立 STA 线程弹出
/// （Kestrel 的请求线程是 MTA，通用对话框要求 STA）。
/// 可写字符串缓冲一律手工分配非托管内存——<see cref="StringBuilder"/> 只能作 P/Invoke 方法参数，
/// 作结构体字段会直接 marshal 失败（判例 2026-10-05：两条通道均 500「系统对话框打不开」）。
/// 框弹出后由置顶线程每 200 ms 扫一次本线程顶层窗（#32770）：未显示则强制显示，并置顶压过全屏窗口——
/// 浏览器占满屏时不依赖抢前台也能看见（做法参照 KKManager 的浏览框，非其代码）。
/// </summary>
public static class NativePicker
{
    /// <summary>OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR。</summary>
    private const int OpenFlags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000008;

    /// <summary>BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE | BIF_EDITBOX。</summary>
    private const uint BrowseFlags = 0x00000001 | 0x00000040 | 0x00000010;

    /// <summary>文件名缓冲字符数（通用对话框要求的可写缓冲）。</summary>
    private const int FileBufferChars = 2048;

    /// <summary>显示名缓冲字符数（BROWSEINFO 要求的可写缓冲）。</summary>
    private const int NameBufferChars = 512;

    /// <summary>目录路径缓冲字符数。</summary>
    private const int PathBufferChars = 1024;

    /// <summary>等待用户操作的上限（毫秒）——超时投递 WM_CLOSE 关掉框并出声。</summary>
    private const int PickTimeoutMs = 300000;

    /// <summary>置顶线程扫描间隔（毫秒）。</summary>
    private const int TopmostIntervalMs = 200;

    /// <summary>Win32 对话框窗口类名——通用对话框的宿主类。</summary>
    private const string DialogClassName = "#32770";

    /// <summary>z-order 目标——置顶。</summary>
    private static readonly IntPtr HwndTopmost = new IntPtr(-1);

    /// <summary>SetWindowPos 标志——不移动。</summary>
    private const uint SwpNoMove = 0x0002;

    /// <summary>SetWindowPos 标志——不改尺寸。</summary>
    private const uint SwpNoSize = 0x0001;

    /// <summary>SetWindowPos 标志——需要时显示。</summary>
    private const uint SwpShowWindow = 0x0040;

    /// <summary>ShowWindow 的 nCmdShow——正常显示。</summary>
    private const int SwShownormal = 1;

    /// <summary>WM_CLOSE 消息号——超时主动关框。</summary>
    private const uint WmClose = 0x0010;

    /// <summary>同时只允许一个选择框——0 空闲 / 1 有框（防叠）。</summary>
    private static int busy;

    /// <summary>置顶线程停止信号。</summary>
    private static volatile bool topmostStop = true;

    /// <summary>弹框线程的本机线程 id（置顶线程按它枚举顶层窗口）。</summary>
    private static uint pickerThreadId;

    /// <summary>本轮扫描命中的候选窗口（优先 #32770）。</summary>
    private static IntPtr topmostCandidate;

    /// <summary>EnumThreadWindows 回调实例——静态持有防 GC。</summary>
    private static readonly EnumThreadProc EnumProcRef = TopmostEnumProc;

    /// <summary>EnumThreadWindows 回调签名。</summary>
    private delegate bool EnumThreadProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>弹「选择音频」对话框——取消返回空串。</summary>
    /// <param name="title">对话框标题。</param>
    /// <returns>选中的文件完整路径；取消为空串。</returns>
    public static Task<string> PickAudioFileAsync(string title)
    {
        return PickAsync(false, title);
    }

    /// <summary>弹「选择图片」对话框——取消返回空串（过滤器与音频共用一张表，靠标题区分意图）。</summary>
    /// <param name="title">对话框标题。</param>
    /// <returns>选中的文件完整路径；取消为空串。</returns>
    public static Task<string> PickImageFileAsync(string title)
    {
        return PickAsync(false, title);
    }

    /// <summary>弹「选择目录」对话框——取消返回空串。</summary>
    /// <param name="title">对话框标题。</param>
    /// <returns>选中的目录完整路径；取消为空串。</returns>
    public static Task<string> PickDirectoryAsync(string title)
    {
        return PickAsync(true, title);
    }

    /// <summary>在独立 STA 线程上弹框并等结果——等待走 Task，不占 Kestrel 请求线程；同刻只允许一个框。</summary>
    /// <param name="directory">true = 选目录 / false = 选文件。</param>
    /// <param name="title">对话框标题。</param>
    /// <returns>选中的路径；取消为空串。</returns>
    private static async Task<string> PickAsync(bool directory, string title)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
        {
            throw new InvalidOperationException("上一个选择框还没关——先关掉它（按 Esc），再点一次");
        }

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(RunDialog(directory, title));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
            finally
            {
                StopTopmostPump();
                Interlocked.Exchange(ref busy, 0);
            }
        });
        thread.IsBackground = true;
        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }

        thread.Start();

        Task<string> pending = completion.Task;
        Task finished = await Task.WhenAny(pending, Task.Delay(PickTimeoutMs)).ConfigureAwait(false);
        if (finished != pending)
        {
            IntPtr stuck = topmostCandidate;
            if (stuck != IntPtr.Zero)
            {
                PostMessage(stuck, WmClose, IntPtr.Zero, IntPtr.Zero);
            }

            throw new TimeoutException("选择框开了 300 秒没操作——已尝试自动关闭；若框还在，按 Esc 关掉再点一次");
        }

        return await pending.ConfigureAwait(false);
    }

    /// <summary>STA 线程内的弹框主体——起置顶线程 → 弹框 → 收置顶线程。</summary>
    /// <param name="directory">true = 选目录 / false = 选文件。</param>
    /// <param name="title">对话框标题。</param>
    /// <returns>选中的路径；取消为空串。</returns>
    private static string RunDialog(bool directory, string title)
    {
        StartTopmostPump(GetCurrentThreadId());
        try
        {
            // 属主窗取控制台窗口：可见且有消息循环，通用对话框据此定位并随之激活
            IntPtr owner = GetConsoleWindow();
            if (directory)
            {
                return ShowBrowseFolder(title, owner);
            }

            return ShowOpenFile(title, owner);
        }
        finally
        {
            StopTopmostPump();
        }
    }

    /// <summary>启动置顶线程——按弹框线程 id 周期扫描并置顶其顶层窗口。</summary>
    /// <param name="threadId">弹框线程的本机线程 id。</param>
    private static void StartTopmostPump(uint threadId)
    {
        pickerThreadId = threadId;
        topmostCandidate = IntPtr.Zero;
        topmostStop = false;
        var pump = new Thread(TopmostPumpLoop);
        pump.IsBackground = true;
        pump.Start();
    }

    /// <summary>停止置顶线程（幂等）。</summary>
    private static void StopTopmostPump()
    {
        topmostStop = true;
    }

    /// <summary>置顶线程主体——每 200 ms 把弹框线程的对话框显示出来并置顶，直到框关闭。</summary>
    private static void TopmostPumpLoop()
    {
        while (!topmostStop)
        {
            Thread.Sleep(TopmostIntervalMs);
            if (topmostStop)
            {
                return;
            }

            topmostCandidate = IntPtr.Zero;
            EnumThreadWindows(pickerThreadId, EnumProcRef, IntPtr.Zero);
            IntPtr dialog = topmostCandidate;
            if (dialog == IntPtr.Zero)
            {
                continue;
            }

            // [段1] 框已创建但未显示——强制显示（创建与显示之间会泵消息，扫描可能插在中间）
            if (!IsWindowVisible(dialog))
            {
                ShowWindow(dialog, SwShownormal);
            }

            // [段2] 持续置顶——幂等，代价可忽略，直到框关闭
            SetWindowPos(dialog, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
        }
    }

    /// <summary>EnumThreadWindows 回调——收集弹框线程的顶层窗口（#32770 优先，其它可见窗备选）。</summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <param name="lParam">未用。</param>
    /// <returns>命中对话框则 false（停止枚举），否则继续。</returns>
    private static bool TopmostEnumProc(IntPtr hWnd, IntPtr lParam)
    {
        var className = new StringBuilder(64);
        GetClassName(hWnd, className, className.Capacity);
        if (string.Equals(className.ToString(), DialogClassName, StringComparison.Ordinal))
        {
            topmostCandidate = hWnd;
            return false;
        }

        if (topmostCandidate == IntPtr.Zero && IsWindowVisible(hWnd))
        {
            topmostCandidate = hWnd;
        }

        return true;
    }

    /// <summary>通用「打开文件」对话框。</summary>
    /// <param name="title">标题。</param>
    /// <param name="owner">属主窗口句柄。</param>
    /// <returns>文件完整路径；取消为空串。</returns>
    private static string ShowOpenFile(string title, IntPtr owner)
    {
        IntPtr fileBuffer = Marshal.AllocHGlobal(FileBufferChars * 2);
        try
        {
            ClearBuffer(fileBuffer, FileBufferChars);
            var dialog = new OpenFileName();
            dialog.lStructSize = Marshal.SizeOf<OpenFileName>();
            dialog.hwndOwner = owner;
            dialog.lpstrFilter = "音频 / 图片\0*.mp3;*.wav;*.m4a;*.flac;*.aac;*.ogg;*.opus;*.wma;*.png;*.jpg;*.jpeg;*.webp;*.bmp\0所有文件\0*.*\0";
            dialog.nFilterIndex = 1;
            dialog.lpstrFile = fileBuffer;
            dialog.nMaxFile = FileBufferChars;
            dialog.lpstrTitle = title;
            dialog.Flags = OpenFlags;

            if (!GetOpenFileNameW(ref dialog))
            {
                return "";
            }

            return Marshal.PtrToStringUni(fileBuffer) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(fileBuffer);
        }
    }

    /// <summary>「选择目录」对话框（shell32）。</summary>
    /// <param name="title">标题。</param>
    /// <param name="owner">属主窗口句柄。</param>
    /// <returns>目录完整路径；取消为空串。</returns>
    private static string ShowBrowseFolder(string title, IntPtr owner)
    {
        IntPtr nameBuffer = Marshal.AllocHGlobal(NameBufferChars * 2);
        try
        {
            ClearBuffer(nameBuffer, NameBufferChars);
            var info = new BrowseInfo();
            info.hwndOwner = owner;
            info.pszDisplayName = nameBuffer;
            info.lpszTitle = title;
            info.ulFlags = BrowseFlags;

            IntPtr idList = SHBrowseForFolderW(ref info);
            if (idList == IntPtr.Zero)
            {
                return "";
            }

            try
            {
                return ReadIdListPath(idList);
            }
            finally
            {
                CoTaskMemFree(idList);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    /// <summary>pidl → 文件系统路径。</summary>
    /// <param name="idList">shell 分配的 pidl。</param>
    /// <returns>目录完整路径；取不到为空串。</returns>
    private static string ReadIdListPath(IntPtr idList)
    {
        IntPtr pathBuffer = Marshal.AllocHGlobal(PathBufferChars * 2);
        try
        {
            ClearBuffer(pathBuffer, PathBufferChars);
            if (!SHGetPathFromIDListW(idList, pathBuffer))
            {
                return "";
            }

            return Marshal.PtrToStringUni(pathBuffer) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(pathBuffer);
        }
    }

    /// <summary>把非托管缓冲清成空串（首字符为 0）。</summary>
    /// <param name="buffer">缓冲首地址。</param>
    /// <param name="chars">字符数。</param>
    private static void ClearBuffer(IntPtr buffer, int chars)
    {
        Marshal.Copy(new char[chars], 0, buffer, chars);
    }

    /// <summary>Win32 OPENFILENAMEW 结构（只用到前段字段，尾部按布局补齐）。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        /// <summary>结构大小。</summary>
        public int lStructSize;

        /// <summary>属主窗口。</summary>
        public IntPtr hwndOwner;

        /// <summary>实例句柄。</summary>
        public IntPtr hInstance;

        /// <summary>过滤器串（\0 分隔）。</summary>
        public string? lpstrFilter;

        /// <summary>自定义过滤器。</summary>
        public string? lpstrCustomFilter;

        /// <summary>自定义过滤器缓冲大小。</summary>
        public int nMaxCustFilter;

        /// <summary>选中的过滤器序号。</summary>
        public int nFilterIndex;

        /// <summary>文件名缓冲（非托管，可写）。</summary>
        public IntPtr lpstrFile;

        /// <summary>文件名缓冲字符数。</summary>
        public int nMaxFile;

        /// <summary>文件名标题缓冲（非托管，未用）。</summary>
        public IntPtr lpstrFileTitle;

        /// <summary>文件名标题缓冲大小。</summary>
        public int nMaxFileTitle;

        /// <summary>起始目录。</summary>
        public string? lpstrInitialDir;

        /// <summary>对话框标题。</summary>
        public string? lpstrTitle;

        /// <summary>标志位。</summary>
        public int Flags;

        /// <summary>文件名在缓冲里的偏移。</summary>
        public short nFileOffset;

        /// <summary>扩展名偏移。</summary>
        public short nFileExtension;

        /// <summary>默认扩展名。</summary>
        public string? lpstrDefExt;

        /// <summary>自定义数据。</summary>
        public IntPtr lCustData;

        /// <summary>钩子。</summary>
        public IntPtr lpfnHook;

        /// <summary>模板名。</summary>
        public string? lpTemplateName;

        /// <summary>保留字段。</summary>
        public IntPtr pvReserved;

        /// <summary>保留字段。</summary>
        public int dwReserved;

        /// <summary>扩展标志。</summary>
        public int flagsEx;
    }

    /// <summary>Win32 BROWSEINFOW 结构。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BrowseInfo
    {
        /// <summary>属主窗口。</summary>
        public IntPtr hwndOwner;

        /// <summary>根 pidl。</summary>
        public IntPtr pidlRoot;

        /// <summary>显示名缓冲（非托管，可写）。</summary>
        public IntPtr pszDisplayName;

        /// <summary>标题。</summary>
        public string? lpszTitle;

        /// <summary>标志位。</summary>
        public uint ulFlags;

        /// <summary>回调。</summary>
        public IntPtr lpfn;

        /// <summary>回调参数。</summary>
        public IntPtr lParam;

        /// <summary>图标序号。</summary>
        public int iImage;
    }

    /// <summary>通用「打开文件」对话框。</summary>
    /// <param name="dialog">对话框结构。</param>
    /// <returns>确认则 true。</returns>
    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OpenFileName dialog);

    /// <summary>「选择目录」对话框。</summary>
    /// <param name="info">结构。</param>
    /// <returns>选中目录的 pidl（需 CoTaskMemFree）。</returns>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolderW(ref BrowseInfo info);

    /// <summary>pidl → 文件系统路径。</summary>
    /// <param name="idList">pidl。</param>
    /// <param name="path">路径缓冲（非托管，可写）。</param>
    /// <returns>成功则 true。</returns>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDListW(IntPtr idList, IntPtr path);

    /// <summary>释放 shell 分配的内存。</summary>
    /// <param name="pointer">指针。</param>
    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pointer);

    /// <summary>枚举指定线程创建的顶层窗口。</summary>
    /// <param name="dwThreadId">线程 id。</param>
    /// <param name="lpfn">回调。</param>
    /// <param name="lParam">回调参数。</param>
    /// <returns>枚举完成则 true。</returns>
    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint dwThreadId, EnumThreadProc lpfn, IntPtr lParam);

    /// <summary>取窗口类名。</summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <param name="lpClassName">类名缓冲。</param>
    /// <param name="nMaxCount">缓冲字符数。</param>
    /// <returns>写入的字符数。</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    /// <summary>窗口是否可见。</summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <returns>可见则 true。</returns>
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>显示窗口。</summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <param name="nCmdShow">显示方式。</param>
    /// <returns>此前可见则 true。</returns>
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>调整窗口 z-order / 位置 / 尺寸。</summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <param name="hWndInsertAfter">z-order 目标。</param>
    /// <param name="x">X 坐标。</param>
    /// <param name="y">Y 坐标。</param>
    /// <param name="cx">宽。</param>
    /// <param name="cy">高。</param>
    /// <param name="uFlags">标志位。</param>
    /// <returns>成功则 true。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    /// <summary>投递窗口消息——超时主动关闭选择框（WM_CLOSE）。</summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <param name="msg">消息号。</param>
    /// <param name="wParam">wParam。</param>
    /// <param name="lParam">lParam。</param>
    /// <returns>投递成功则 true。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>取当前线程的本机 id。</summary>
    /// <returns>线程 id。</returns>
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>取本进程的控制台窗口句柄（无则返回零）。</summary>
    /// <returns>窗口句柄。</returns>
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
}
