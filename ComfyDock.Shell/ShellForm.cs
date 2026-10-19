using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ComfyDock.Shell;

/// <summary>
/// 套壳主窗口——左侧账号表，右侧当前账号的 WebView2。
/// 每个账号一份独立的浏览器数据目录（profile），登录态存在里面：人工登录与自动采集共用同一份数据。
/// </summary>
public sealed class ShellForm : Form
{
    /// <summary>导航等待上限（毫秒）。</summary>
    private const int NavigationTimeoutMs = 30000;

    /// <summary>单次脚本求值上限（毫秒）——页面卡住时不再无限等。</summary>
    private const int ScriptTimeoutMs = 15000;

    /// <summary>浏览器启动参数——禁后台节流，窗口藏起来时页面照常加载与计时。</summary>
    private const string BrowserArguments =
        "--disable-background-timer-throttling --disable-backgrounding-occluded-windows --disable-renderer-backgrounding";

    /// <summary>账号首页地址。</summary>
    private const string HomeUrl = "https://www.runninghub.cn/";

    /// <summary>关闭视图后等待浏览器进程释放文件的时间（毫秒）。</summary>
    private const int ProfileReleaseWaitMs = 1200;

    private readonly ShellOptions _options;
    private readonly AccountRegistry _registry;
    private readonly ListBox _accountList = new ListBox();
    private readonly Panel _viewHost = new Panel();
    private readonly ToolStripStatusLabel _statusLabel = new ToolStripStatusLabel();
    private readonly Dictionary<string, AccountView> _views = new Dictionary<string, AccountView>();

    private string _currentId = "";

    /// <summary>以选项构造窗口并载入账号表。</summary>
    /// <param name="options">套壳选项。</param>
    public ShellForm(ShellOptions options)
    {
        _options = options;
        _registry = new AccountRegistry(options);
        BuildUi();
        ReloadAccounts();

        if (_options.Bridge)
        {
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
        }
    }

    /// <summary>当前账号表。</summary>
    public IReadOnlyList<ShellAccount> Accounts
    {
        get { return _registry.Accounts; }
    }

    /// <summary>最近一次错误说明（供桥接层回报）。</summary>
    public string LastError { get; private set; } = "";

    // [段1] 界面搭建
    private void BuildUi()
    {
        Text = "ComfyDock 浏览器";
        Width = 1320;
        Height = 880;
        MinimumSize = new Size(900, 620);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(248, 248, 250);

        _viewHost.Dock = DockStyle.Fill;
        _viewHost.BackColor = Color.White;
        Controls.Add(_viewHost);

        Panel left = new Panel();
        left.Dock = DockStyle.Left;
        left.Width = 240;
        left.Padding = new Padding(8);
        left.BackColor = Color.FromArgb(240, 242, 246);

        _accountList.Dock = DockStyle.Fill;
        _accountList.IntegralHeight = false;
        _accountList.ItemHeight = 26;
        _accountList.Font = new Font("Microsoft YaHei UI", 10F);
        _accountList.DoubleClick += OnAccountListDoubleClick;
        left.Controls.Add(_accountList);
        Controls.Add(left);

        ToolStrip toolbar = new ToolStrip();
        toolbar.GripStyle = ToolStripGripStyle.Hidden;
        toolbar.ImageScalingSize = new Size(16, 16);

        ToolStripButton reloadButton = new ToolStripButton("重载账号表");
        reloadButton.Click += OnReloadClick;
        toolbar.Items.Add(reloadButton);

        ToolStripButton openButton = new ToolStripButton("打开首页");
        openButton.Click += OnOpenHomeClick;
        toolbar.Items.Add(openButton);

        ToolStripButton reloadPageButton = new ToolStripButton("刷新页面");
        reloadPageButton.Click += OnReloadPageClick;
        toolbar.Items.Add(reloadPageButton);

        Controls.Add(toolbar);

        StatusStrip status = new StatusStrip();
        status.Items.Add(_statusLabel);
        Controls.Add(status);
    }

    // [段2] 账号表渲染
    private void ReloadAccounts()
    {
        _accountList.BeginUpdate();
        _accountList.Items.Clear();
        foreach (ShellAccount account in _registry.Accounts)
        {
            if (!account.Enabled)
            {
                continue;
            }

            _accountList.Items.Add(new AccountRow(account));
        }

        _accountList.EndUpdate();
        SetStatus("账号 " + _accountList.Items.Count + " 个 · 数据目录 " + _options.DataDir);
    }

    /// <summary>重新读账号表并刷新列表（供桥接层调用）。</summary>
    public void ReloadRegistry()
    {
        _registry.Load();
        ReloadAccounts();
    }

    /// <summary>把窗口提到最前。</summary>
    public void BringToFrontSafe()
    {
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Show();
        Activate();
        BringToFront();
    }

    /// <summary>把窗口搬到屏幕中央并显示——只在有人要看网站时调用；后台刷新不动窗口。</summary>
    public void ShowWindow()
    {
        ShowInTaskbar = true;
        Rectangle area = Screen.PrimaryScreen == null
            ? new Rectangle(0, 0, Width, Height)
            : Screen.PrimaryScreen.WorkingArea;
        Location = new Point(area.X + ((area.Width - Width) / 2), area.Y + ((area.Height - Height) / 2));
        BringToFrontSafe();
    }

    // [段3] 视图创建与切换
    private async Task<AccountView> EnsureViewAsync(ShellAccount account)
    {
        AccountView? existing;
        if (_views.TryGetValue(account.Id, out existing))
        {
            return existing;
        }

        await CloseOthersAsync(account.Id);

        string profileDir = _registry.ResolveProfileDir(account);
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(_options.CacheDir);

        string importSource = profileDir;
        if (!string.IsNullOrWhiteSpace(account.ImportFrom))
        {
            importSource = account.ImportFrom;
        }

        ProfileMigrator.Result migration = ProfileMigrator.Import(importSource, profileDir);

        WebView2 view = new WebView2();
        view.Dock = DockStyle.Fill;
        view.Visible = false;
        _viewHost.Controls.Add(view);

        CoreWebView2EnvironmentOptions environmentOptions = new CoreWebView2EnvironmentOptions(
            BrowserArguments + " --disk-cache-dir=\"" + _options.CacheDir + "\"");
        CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, profileDir, environmentOptions);
        await view.EnsureCoreWebView2Async(environment);
        view.CoreWebView2.Settings.IsStatusBarEnabled = false;
        view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;

        AccountView created = new AccountView();
        created.View = view;
        created.Environment = environment;
        created.ProfileDir = profileDir;
        created.MigrationNote = migration.Note;
        _views[account.Id] = created;
        return created;
    }

    private void ShowAccount(string accountId)
    {
        _currentId = accountId;
        foreach (KeyValuePair<string, AccountView> pair in _views)
        {
            bool visible = string.Equals(pair.Key, accountId, StringComparison.Ordinal);
            pair.Value.View.Visible = visible;
        }
    }

    /// <summary>关掉除目标账号以外的全部视图——共用缓存目录同一时刻只让一个浏览器进程持有。</summary>
    /// <param name="keepAccountId">要保留的账号 id。</param>
    /// <returns>关闭完成的任务。</returns>
    private async Task CloseOthersAsync(string keepAccountId)
    {
        List<string> others = new List<string>();
        foreach (KeyValuePair<string, AccountView> pair in _views)
        {
            if (!string.Equals(pair.Key, keepAccountId, StringComparison.Ordinal))
            {
                others.Add(pair.Key);
            }
        }

        foreach (string id in others)
        {
            Close(id);
        }

        if (others.Count > 0)
        {
            await Task.Delay(ProfileReleaseWaitMs);
        }
    }

    // [段4] 桥接层可调用的操作
    /// <summary>打开某账号的指定地址（空 = 首页）并等待导航结束。</summary>
    /// <param name="accountId">账号 id。</param>
    /// <param name="url">目标地址。</param>
    /// <returns>导航结果（成功与否 / 标题 / 最终地址）。</returns>
    public async Task<object> OpenAsync(string accountId, string url, bool visible)
    {
        ShellAccount? account = _registry.Find(accountId);
        if (account == null)
        {
            throw new InvalidOperationException("账号不存在：" + accountId);
        }

        AccountView view = await EnsureViewAsync(account);
        ShowAccount(accountId);
        if (visible)
        {
            ShowWindow();
        }

        string target = string.IsNullOrWhiteSpace(url) ? HomeUrl : url;
        bool ok = await NavigateAsync(view, target);
        SetStatus((ok ? "已打开 " : "导航失败 ") + account.Name + " · " + target);

        return new
        {
            navigated = ok,
            title = view.View.CoreWebView2 == null ? "" : view.View.CoreWebView2.DocumentTitle,
            url = view.View.CoreWebView2 == null ? target : view.View.CoreWebView2.Source,
            profileDir = view.ProfileDir,
        };
    }

    /// <summary>装「新文档创建时执行」的脚本（同一视图重复安装自动去重）。</summary>
    /// <param name="accountId">账号 id。</param>
    /// <param name="script">脚本正文。</param>
    /// <returns>安装结果。</returns>
    public async Task<object> AddInitScriptAsync(string accountId, string script)
    {
        ShellAccount? account = _registry.Find(accountId);
        if (account == null)
        {
            throw new InvalidOperationException("账号不存在：" + accountId);
        }

        AccountView view = await EnsureViewAsync(account);
        if (view.InitHooks.Contains(script))
        {
            return new { added = false };
        }

        await view.View.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script);
        view.InitHooks.Add(script);
        return new { added = true };
    }

    /// <summary>在某账号页面内执行脚本并回传结果（结果是 JS 值的 JSON 文本）。</summary>
    /// <param name="accountId">账号 id。</param>
    /// <param name="script">脚本正文。</param>
    /// <returns>执行结果。</returns>
    public async Task<object> EvalAsync(string accountId, string script)
    {
        ShellAccount? account = _registry.Find(accountId);
        if (account == null)
        {
            throw new InvalidOperationException("账号不存在：" + accountId);
        }

        AccountView view = await EnsureViewAsync(account);
        string value = await EvalWithTimeoutAsync(view, script, ScriptTimeoutMs);
        return new
        {
            value,
            url = view.View.CoreWebView2 == null ? "" : view.View.CoreWebView2.Source,
        };
    }

    /// <summary>在账号视图上调用一条 DevTools 协议方法（页面 JS 做不到的动作，如给文件框塞本地文件）。</summary>
    /// <param name="accountId">账号 id。</param>
    /// <param name="method">协议方法名。</param>
    /// <param name="parameters">参数 JSON（空 = 空对象）。</param>
    /// <returns>协议方法的结果 JSON 文本。</returns>
    public async Task<object> CallDevToolsAsync(string accountId, string method, string parameters)
    {
        ShellAccount? account = _registry.Find(accountId);
        if (account == null)
        {
            throw new InvalidOperationException("账号不存在：" + accountId);
        }

        AccountView view = await EnsureViewAsync(account);
        CoreWebView2? core = view.View.CoreWebView2;
        if (core == null)
        {
            throw new InvalidOperationException("浏览器核心尚未就绪");
        }

        string payload = string.IsNullOrWhiteSpace(parameters) ? "{}" : parameters;
        string result = await core.CallDevToolsProtocolMethodAsync(method, payload);
        return new { result };
    }

    /// <summary>在某账号页面内执行脚本并等待某个表达式成立（轮询求值）。</summary>
    /// <param name="accountId">账号 id。</param>
    /// <param name="script">每轮执行的脚本。</param>
    /// <param name="timeoutMs">等待上限（毫秒）。</param>
    /// <returns>最后一次执行结果。</returns>
    public async Task<object> WaitAsync(string accountId, string script, int timeoutMs)
    {
        ShellAccount? account = _registry.Find(accountId);
        if (account == null)
        {
            throw new InvalidOperationException("账号不存在：" + accountId);
        }

        AccountView view = await EnsureViewAsync(account);
        int waitLimit = timeoutMs <= 0 ? NavigationTimeoutMs : timeoutMs;
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(waitLimit);
        string last = "";

        while (DateTime.UtcNow < deadline)
        {
            last = await EvalWithTimeoutAsync(view, script, ScriptTimeoutMs);
            if (last != null && last != "false" && last != "null" && last != "\"\"")
            {
                return new { matched = true, value = last };
            }

            await Task.Delay(400);
        }

        return new { matched = false, value = last };
    }

    /// <summary>关闭某账号的视图（登录态留在 profile 里不动）。</summary>
    /// <param name="accountId">账号 id。</param>
    /// <returns>是否真的关掉了。</returns>
    public object Close(string accountId)
    {
        AccountView? view;
        if (!_views.TryGetValue(accountId, out view))
        {
            return new { closed = false };
        }

        _viewHost.Controls.Remove(view.View);
        view.View.Dispose();
        _views.Remove(accountId);
        if (string.Equals(_currentId, accountId, StringComparison.Ordinal))
        {
            _currentId = "";
        }

        SetStatus("已关闭账号视图：" + accountId);
        return new { closed = true };
    }

    /// <summary>关闭视图并按需删除该账号的浏览器数据目录。</summary>
    /// <param name="accountId">账号 id。</param>
    /// <param name="deleteProfile">是否删除 profile 目录。</param>
    /// <returns>关闭与删除结果。</returns>
    public async Task<object> RemoveAsync(string accountId, bool deleteProfile)
    {
        ShellAccount? account = _registry.Find(accountId);
        string profileDir = account == null ? "" : _registry.ResolveProfileDir(account);

        AccountView? view;
        if (_views.TryGetValue(accountId, out view))
        {
            _viewHost.Controls.Remove(view.View);
            view.View.Dispose();
            _views.Remove(accountId);
            if (string.Equals(_currentId, accountId, StringComparison.Ordinal))
            {
                _currentId = "";
            }
        }

        if (!deleteProfile || string.IsNullOrWhiteSpace(profileDir))
        {
            return new { closed = true, deleted = false, profileDir };
        }

        await Task.Delay(ProfileReleaseWaitMs);
        try
        {
            if (Directory.Exists(profileDir))
            {
                Directory.Delete(profileDir, true);
            }

            SetStatus("已删除账号数据：" + profileDir);
            return new { closed = true, deleted = true, profileDir };
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            SetStatus("删除账号数据失败：" + profileDir);
            return new { closed = true, deleted = false, profileDir, error = LastError };
        }
    }

    /// <summary>当前已创建的视图（账号 id 列表）。</summary>
    /// <returns>账号 id 列表。</returns>
    public List<string> OpenedAccountIds()
    {
        List<string> list = new List<string>();
        foreach (KeyValuePair<string, AccountView> pair in _views)
        {
            list.Add(pair.Key);
        }

        return list;
    }

    /// <summary>写状态栏。</summary>
    /// <param name="text">状态文本。</param>
    public void SetStatus(string text)
    {
        _statusLabel.Text = text;
    }

    // [段5] 导航
    private async Task<bool> NavigateAsync(AccountView view, string url)
    {
        CoreWebView2 core = view.View.CoreWebView2;
        TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = null!;
        handler = delegate (object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            core.NavigationCompleted -= handler;
            completion.TrySetResult(args.IsSuccess);
        };

        core.NavigationCompleted += handler;
        core.Navigate(url);

        Task finished = await Task.WhenAny(completion.Task, Task.Delay(NavigationTimeoutMs));
        if (finished != completion.Task)
        {
            core.NavigationCompleted -= handler;
            LastError = "导航超时：" + url;
            return false;
        }

        return await completion.Task;
    }

    /// <summary>带超时的脚本求值——页面在忙时 ExecuteScriptAsync 可能长时间不返回。</summary>
    /// <param name="view">账号视图。</param>
    /// <param name="script">脚本正文。</param>
    /// <param name="timeoutMs">超时毫秒。</param>
    /// <returns>脚本结果（JS 值的 JSON 文本）。</returns>
    private static async Task<string> EvalWithTimeoutAsync(AccountView view, string script, int timeoutMs)
    {
        CoreWebView2? core = view.View.CoreWebView2;
        if (core == null)
        {
            throw new InvalidOperationException("浏览器核心尚未就绪");
        }

        Task<string> evaluation = core.ExecuteScriptAsync(script);
        Task finished = await Task.WhenAny(evaluation, Task.Delay(timeoutMs));
        if (finished != evaluation)
        {
            throw new TimeoutException("脚本求值超时（" + timeoutMs + " 毫秒）");
        }

        return await evaluation;
    }

    // [段6] 界面事件
    private void OnAccountListDoubleClick(object? sender, EventArgs e)
    {
        AccountRow? row = _accountList.SelectedItem as AccountRow;
        if (row == null)
        {
            return;
        }

        OpenFromUiAsync(row.AccountId);
    }

    private void OnOpenHomeClick(object? sender, EventArgs e)
    {
        AccountRow? row = _accountList.SelectedItem as AccountRow;
        if (row == null)
        {
            SetStatus("先在左侧选一个账号");
            return;
        }

        OpenFromUiAsync(row.AccountId);
    }

    private void OnReloadClick(object? sender, EventArgs e)
    {
        ReloadRegistry();
    }

    private void OnReloadPageClick(object? sender, EventArgs e)
    {
        AccountView? view;
        if (!_views.TryGetValue(_currentId, out view))
        {
            SetStatus("当前没有打开的账号视图");
            return;
        }

        if (view.View.CoreWebView2 != null)
        {
            view.View.CoreWebView2.Reload();
        }
    }

    private async void OpenFromUiAsync(string accountId)
    {
        try
        {
            await OpenAsync(accountId, HomeUrl, true);
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            SetStatus("打开失败：" + LastError);
        }
    }

    // [段7] 内部类型
    /// <summary>一个账号的浏览器视图与其数据目录。</summary>
    private sealed class AccountView
    {
        /// <summary>WebView2 控件。</summary>
        public WebView2 View = null!;

        /// <summary>该账号独占的内核环境（数据目录互不共用）。</summary>
        public CoreWebView2Environment Environment = null!;

        /// <summary>该账号的浏览器数据目录（绝对路径）。</summary>
        public string ProfileDir = "";

        /// <summary>首次导入时的结果说明（空 = 未走导入）。</summary>
        public string MigrationNote = "";

        /// <summary>已安装的「新文档创建时执行」脚本（去重用）。</summary>
        public HashSet<string> InitHooks = new HashSet<string>();
    }

    /// <summary>账号列表的一行。</summary>
    private sealed class AccountRow
    {
        /// <summary>以账号条目构造。</summary>
        /// <param name="account">账号条目。</param>
        public AccountRow(ShellAccount account)
        {
            AccountId = account.Id;
            Display = string.IsNullOrWhiteSpace(account.Name) ? account.Id : account.Name;
            if (!string.IsNullOrWhiteSpace(account.Note))
            {
                Display = Display + "  " + account.Note;
            }
        }

        /// <summary>账号 id。</summary>
        public string AccountId { get; private set; }

        /// <summary>列表显示文本。</summary>
        public string Display { get; private set; }

        /// <summary>列表渲染用文本。</summary>
        /// <returns>显示文本。</returns>
        public override string ToString()
        {
            return Display;
        }
    }
}
