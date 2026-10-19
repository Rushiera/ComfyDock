using System;
using System.IO;
using System.Windows.Forms;

namespace ComfyDock.Shell;

/// <summary>
/// 套壳入口——WinForms 界面 + 可选的管道桥（由主程序拉起时开桥，双击启动时不开）。
/// 另有探针分支：<c>--probe &lt;Chrome 用户目录&gt;</c> 导入既有登录态并回报判据。
/// </summary>
internal static class Program
{
    /// <summary>进程入口。</summary>
    /// <param name="args">命令行参数：<c>--data &lt;目录&gt;</c> · <c>--bridge</c> · <c>--probe &lt;目录&gt;</c>。</param>
    [STAThread]
    private static int Main(string[] args)
    {
        ShellOptions options;
        try
        {
            options = ShellOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "ComfyDock 浏览器", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        Directory.CreateDirectory(options.DataDir);
        Directory.CreateDirectory(options.AuthDir);
        Directory.CreateDirectory(options.SharedDir);
        Directory.CreateDirectory(options.CacheDir);
        Directory.CreateDirectory(options.LogDir);

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (!string.IsNullOrWhiteSpace(options.ProbeProfileDir))
        {
            return ShellProbe.Run(options);
        }

        ShellForm form = new ShellForm(options);
        if (options.Bridge)
        {
            ShellBridge bridge = new ShellBridge(form, options);
            form.Shown += delegate
            {
                bridge.Start();
            };
        }

        Application.Run(form);
        return 0;
    }
}
