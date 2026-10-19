using System;
using System.IO;

namespace ComfyDock.Shell;

/// <summary>
/// 套壳命令行选项——数据根、账号表落点、数据区划分、探针参数。
/// 数据区分两块：<c>auth\</c> = 登录态（每账号一份，独占）；<c>shared\</c> = 可复用数据（全体共用一份）。
/// </summary>
public sealed class ShellOptions
{
    /// <summary>数据根目录（账号表、登录态、可复用数据都在其下）。</summary>
    public string DataDir { get; set; } = "";

    /// <summary>是否开启标准输入输出桥（由主程序拉起时为真；双击启动时为假）。</summary>
    public bool Bridge { get; set; }

    /// <summary>探针源目录（Chrome 形态用户目录）；空 = 不起探针分支。</summary>
    public string ProbeProfileDir { get; set; } = "";

    /// <summary>账号表完整路径。</summary>
    public string RegistryPath
    {
        get { return Path.Combine(DataDir, "accounts-registry.json"); }
    }

    /// <summary>登录态根目录——每账号一个子目录（WebView2 用户数据目录）。</summary>
    public string AuthDir
    {
        get { return Path.Combine(DataDir, "auth"); }
    }

    /// <summary>可复用数据根目录——全体账号共用一份。</summary>
    public string SharedDir
    {
        get { return Path.Combine(DataDir, "shared"); }
    }

    /// <summary>共用的磁盘缓存目录（各账号浏览器进程都指向它）。</summary>
    public string CacheDir
    {
        get { return Path.Combine(SharedDir, "cache"); }
    }

    /// <summary>套壳日志目录。</summary>
    public string LogDir
    {
        get { return Path.Combine(DataDir, "shell"); }
    }

    /// <summary>解析命令行参数。</summary>
    /// <param name="args">原始参数数组。</param>
    /// <returns>选项对象；未知参数或非法值抛异常。</returns>
    public static ShellOptions Parse(string[] args)
    {
        ShellOptions options = new ShellOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            if (string.Equals(key, "--data", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("参数缺值：" + key);
                }

                i = i + 1;
                options.DataDir = args[i];
            }
            else if (string.Equals(key, "--bridge", StringComparison.Ordinal))
            {
                options.Bridge = true;
            }
            else if (string.Equals(key, "--probe", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("参数缺值：" + key);
                }

                i = i + 1;
                options.ProbeProfileDir = args[i];
            }
            else
            {
                throw new ArgumentException("未知参数：" + key);
            }
        }

        if (string.IsNullOrWhiteSpace(options.DataDir))
        {
            options.DataDir = Path.Combine(AppContext.BaseDirectory, "data");
        }

        return options;
    }
}
