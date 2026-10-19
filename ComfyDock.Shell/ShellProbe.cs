using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ComfyDock.Shell;

/// <summary>
/// 探针——把一份 Chrome 形态用户目录导入临时 WebView2 数据目录，打开站点，回报登录态判据。
/// 只读源目录；结果落 <c>data\probe-result.json</c> 并打到标准输出。
/// </summary>
internal static class ShellProbe
{
    /// <summary>探针目标页。</summary>
    private const string ProbeUrl = "https://www.runninghub.cn/";

    /// <summary>登录态判据脚本——把页面可判读的证据一次性带回来。</summary>
    private const string ProbeScript =
        "(function(){var s=document.getElementById('__NUXT_DATA__');var p=s?s.textContent:'';" +
        "var i=p.indexOf('totalCoin');var j=p.indexOf('srh-login-user-info');" +
        "var body=document.body?document.body.innerText:'';" +
        "return JSON.stringify({title:document.title,ready:document.readyState," +
        "cookieLen:document.cookie.length,payloadLen:p.length,hasUser:i>=0," +
        "coinCtx:i>=0?p.slice(Math.max(0,i-60),i+60):''," +
        "loginCtx:j>=0?p.slice(Math.max(0,j),j+160):'',head:body.slice(0,160)});})()";

    /// <summary>环境创建与导航就绪等待上限（毫秒）。</summary>
    private const int ReadyTimeoutMs = 45000;

    /// <summary>单次脚本求值上限（毫秒）——求值本身挂起时不再无限等。</summary>
    private const int EvalTimeoutMs = 6000;

    /// <summary>硬超时（毫秒）——到点无论跑到哪一步都写结果并退出。</summary>
    private const int HardTimeoutMs = 110000;

    /// <summary>执行探针。</summary>
    /// <param name="options">套壳选项（含探针源目录）。</param>
    /// <returns>进程退出码。</returns>
    public static int Run(ShellOptions options)
    {
        string probeDir = Path.Combine(options.DataDir, "shared", "probe");
        string userDataFolder = Path.Combine(probeDir, "udf-" + DateTime.Now.ToString("yyyyMMddHHmmssfff"));

        Directory.CreateDirectory(userDataFolder);
        Directory.CreateDirectory(options.CacheDir);

        ProfileMigrator.Result migration = ProfileMigrator.Import(options.ProbeProfileDir, userDataFolder);

        Form form = new Form();
        form.Text = "ComfyDock 探针";
        form.Width = 1200;
        form.Height = 820;
        form.StartPosition = FormStartPosition.CenterScreen;

        WebView2 view = new WebView2();
        view.Dock = DockStyle.Fill;
        form.Controls.Add(view);

        string scriptResult = "";
        string failure = "";
        bool navigated = false;
        bool finished = false;

        System.Windows.Forms.Timer watchdog = new System.Windows.Forms.Timer();
        watchdog.Interval = HardTimeoutMs;
        watchdog.Tick += delegate
        {
            watchdog.Stop();
            if (!finished)
            {
                finished = true;
                WriteResult(options, migration, navigated, scriptResult, failure + "；硬超时");
                form.Close();
            }
        };
        watchdog.Start();

        form.Shown += async delegate
        {
                CoreWebView2Environment? environment = null;
                try
                {
                    environment = await WithTimeoutAsync(
                        CoreWebView2Environment.CreateAsync(null, userDataFolder, null),
                        25000,
                        "创建内核环境");
                await WithTimeoutAsync(view.EnsureCoreWebView2Async(environment), 25000, "初始化 WebView2");

                view.CoreWebView2.Navigate(ProbeUrl);
                navigated = await WaitForPayloadAsync(view, ReadyTimeoutMs);
                if (!navigated)
                {
                    failure = "页面未在时限内出现 Nuxt payload";
                }

                string? evaluated = await TryEvalAsync(view, ProbeScript, EvalTimeoutMs);
                scriptResult = evaluated == null ? "" : evaluated;
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                watchdog.Stop();
                if (!finished)
                {
                    finished = true;
                    WriteResult(options, migration, navigated, scriptResult, failure);
                }

                try
                {
                    view.Dispose();
                }
                catch (Exception)
                {
                }

                form.Close();
            }
        };

        Application.Run(form);
        return 0;
    }

    // [段1] 等待与求值
    private static async Task<bool> WaitForPayloadAsync(WebView2 view, int timeoutMs)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            string? value = await TryEvalAsync(
                view,
                "(function(){var s=document.getElementById('__NUXT_DATA__');return s?s.textContent.length:0;})()",
                EvalTimeoutMs);
            if (!string.IsNullOrEmpty(value))
            {
                string trimmed = value.Trim();
                if (trimmed != "0" && trimmed != "null")
                {
                    return true;
                }
            }

            await Task.Delay(800);
        }

        return false;
    }

    private static async Task<string?> TryEvalAsync(WebView2 view, string script, int timeoutMs)
    {
        if (view.CoreWebView2 == null)
        {
            return null;
        }

        Task<string> evaluation = view.CoreWebView2.ExecuteScriptAsync(script);
        Task finished = await Task.WhenAny(evaluation, Task.Delay(timeoutMs));
        if (finished != evaluation)
        {
            return null;
        }

        return await evaluation;
    }

    private static async Task<T> WithTimeoutAsync<T>(Task<T> work, int timeoutMs, string label)
    {
        Task finished = await Task.WhenAny(work, Task.Delay(timeoutMs));
        if (finished != work)
        {
            throw new TimeoutException(label + "超时");
        }

        return await work;
    }

    private static async Task WithTimeoutAsync(Task work, int timeoutMs, string label)
    {
        Task finished = await Task.WhenAny(work, Task.Delay(timeoutMs));
        if (finished != work)
        {
            throw new TimeoutException(label + "超时");
        }

        await work;
    }

    // [段2] 结果落盘
    private static void WriteResult(ShellOptions options, ProfileMigrator.Result migration, bool navigated, string scriptResult, string failure)
    {
        Dictionary<string, object?> payload = new Dictionary<string, object?>();
        payload["probeProfileDir"] = options.ProbeProfileDir;
        payload["migrated"] = migration.Imported;
        payload["migrateNote"] = migration.Note;
        payload["copied"] = migration.Copied;
        payload["skipped"] = migration.Skipped;
        payload["navigated"] = navigated;
        payload["failure"] = failure;
        payload["script"] = scriptResult;

        JsonSerializerOptions writeOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        string json = JsonSerializer.Serialize(payload, writeOptions);
        try
        {
            File.WriteAllText(Path.Combine(options.DataDir, "probe-result.json"), json);
        }
        catch (IOException)
        {
        }

        Console.Out.WriteLine(json);
        Console.Out.Flush();
    }
}
