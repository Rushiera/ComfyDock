using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ComfyDock.Collect;
using ComfyDock.Local;
using ComfyDock.Models;
using ComfyDock.Moss;
using ComfyDock.Omni;
using ComfyDock.RunningHub;
using ComfyDock.Rvc;
using ComfyDock.Shortcut;
using ComfyDock.Store;
using ComfyDock.Tagger;
using ComfyDock.Tts;
using ComfyDock.Vits;

namespace ComfyDock;

/// <summary>
/// 程序入口——起本地面板服务（Kestrel + 静态页 + Minimal API），可选自动打开浏览器。
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>进程入口。</summary>
    /// <param name="args">命令行参数。</param>
    /// <returns>退出码（0 正常 / 2 参数或环境错误）。</returns>
    public static async Task<int> Main(string[] args)
    {
        AppOptions options;
        try
        {
            options = AppOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("参数错误：" + ex.Message);
            return 2;
        }

        // [段0] 线上识别 CLI——不经面板跑一次（试跑 / 排错用；走 HTTP，不需要 chrome）
        if (options.RhSubmit.Length > 0 || options.RhPoll || options.RhStatus)
        {
            return await RhCli.RunAsync(options).ConfigureAwait(false);
        }

        // [段0b] latent 解码 CLI——内置解码器试跑（不连本机 ComfyUI、不起外部进程）
        if (options.LatentDecode.Length > 0)
        {
            return LatentCli.Run(options.LatentDecode);
        }

        // [段0c] 自建中间态 CLI——latent ⇄ `.cdlat`（同上，纯内置编解码）
        if (options.CdlatPack.Length > 0)
        {
            return LatentCli.RunPack(options.CdlatPack);
        }

        if (options.CdlatUnpack.Length > 0)
        {
            return LatentCli.RunUnpack(options.CdlatUnpack);
        }

        // [段0d] WD 打标自检 CLI——同一套引擎代码路径，单图跑通并打印结果（不占端口、不要 chrome）
        if (options.TaggerSelfTest.Length > 0)
        {
            return await TaggerCli.RunAsync(
                options.TaggerSelfTest,
                options.LocalComfyRoot).ConfigureAwait(false);
        }

        // [段0d2] 采集自检 CLI——账号表迁移 + 套壳采集整链跑一遍后退出（不占端口）
        if (options.CollectSelfTest.Length > 0)
        {
            return await CollectCli.RunAsync(options, options.CollectSelfTest).ConfigureAwait(false);
        }

        // [段0d3] 后台刷新 CLI——所有启用账号刷一遍并落盘，不弹窗口、不占端口
        if (options.RefreshAll)
        {
            return await RefreshCli.RunAsync(options).ConfigureAwait(false);
        }

        if (options.RvcSelfTest.Length > 0)
        {
            return await RvcCli.RunAsync(
                options.RvcSelfTest,
                options.RvcPython,
                options.RvcRepo,
                options.RvcModels,
                options.RvcModel,
                options.RvcOutDir).ConfigureAwait(false);
        }

        // [段0e] RVC 实时链自检 CLI（线 3）——枚举设备 → 3 秒正弦进链 → 打印延迟；不动麦克风
        if (options.RvcRtSelfTest.Length > 0)
        {
            return await RvcCli.RunRealtimeAsync(
                options.RvcRtSelfTest,
                options.RvcPython,
                options.RvcRepo,
                options.RvcModels,
                options.RvcModel,
                options.RvcOutDir).ConfigureAwait(false);
        }

        // [段0e2] VITS 自检 CLI——起常驻 worker 合成一句（可选换一次音色）后打印结果并退出
        if (options.VitsSelfTest.Length > 0)
        {
            return await VitsCli.RunAsync(
                options.VitsSelfTest,
                options.VitsPython,
                options.VitsScript,
                options.VitsModels,
                options.VitsText,
                options.VitsOutDir,
                options.VitsVcAudio,
                0,
                1).ConfigureAwait(false);
        }

        if (options.OmniSelfTest.Length > 0)
        {
            return await OmniCli.RunAsync(
                options.OmniSelfTest,
                options.OmniCli,
                options.OmniModel,
                options.OmniMmproj,
                options.OmniPython,
                options.OmniOutDir).ConfigureAwait(false);
        }

        if (!File.Exists(options.ChromePath))
        {
            Console.Error.WriteLine("找不到 chrome：" + options.ChromePath + "（用 --chrome 指定）");
            return 2;
        }

        // [段1] 启动守卫——端口被旧实例占着就结束它，新实例直接接管
        StartupGuardResult guard = StartupGuard.Release(options.Port);
        if (!guard.Ok)
        {
            Console.Error.WriteLine(guard.Message);
            return 2;
        }

        if (guard.Message.Length > 0)
        {
            Console.WriteLine(guard.Message);
        }

        var store = new AccountStore(options.SnapshotPath);
        var registry = new AccountRegistryStore(options.RegistryPath, options.DataDir);

        // [段2] 首次运行：从旧的快捷方式目录迁移账号表（只建一次；已有账号表则原样不动）
        int migrated = registry.EnsureMigrated(options.GoogleRoot, store.Load());
        if (migrated > 0)
        {
            Console.WriteLine("账号表已从快捷方式目录迁移 " + migrated + " 条");
        }

        var hub = new BrowserHub(options.ChromePath, Path.Combine(options.DataDir, "browsers.json"));
        var gateway = new CdpGateway(options.ChromePath, hub);
        var shell = new ShellClient(options.ShellPath, options.DataDir);
        var service = new CollectService(options, new Collector(shell, options.Url, options.WorkspaceUrl), store, registry);

        // [段2b] 快照按账号表校正——旧的快捷方式行清掉，新增账号补占位行（启动即对齐，面板不再有幽灵行）
        service.SyncWithRegistry();
        var local = new LocalComfy(options.LocalComfyUrl, options.LocalComfyLauncher);

        // [段3] 线上识别服务——账号复用 accounts.json；后台轮询循环随面板启动
        var rh = new RhService(
            service,
            Path.Combine(options.DataDir, "rh-config.json"),
            Path.Combine(options.DataDir, "rh-ledger.json"),
            options.RhOutDir);
        rh.Start();

        // [段3b] 本地生成（MOSS-Image）——依赖索引 / 下载 / 插件 / 生成，全部走本机 ComfyUI 的 HTTP API
        var moss = new MossService(options.LocalComfyRoot, options.LocalComfyUrl, options.DataDir, options.MossOutDir);

        // [段3c] 本地生成（IrodoriTTS）——常驻 worker 跑 Irodori-TTS，基座 + 声码器 + LoRA 只加载一次
        var tts = new TtsService(
            new TtsConfig
            {
                PythonPath = options.TtsPython,
                RepoDir = options.TtsRepo,
                BaseModel = options.TtsBaseModel,
                CodecWeights = options.TtsCodec,
                LoraDir = options.TtsLora,
                RefsDir = options.TtsRefsDir,
                LargeBaseModel = options.TtsLargeBaseModel,
                LargeInt8BaseModel = options.TtsLargeInt8BaseModel,
                LargeLoraDir = options.TtsLargeLora,
            },
            options.DataDir,
            options.TtsOutDir);

        // [段3d] 本地生成（WD Tagger 打标）——模型住 ComfyUI 的 models\onnx，推理借 python_embeded 跑常驻 worker
        var tagger = new TaggerService(
            new TaggerConfig
            {
                ComfyRoot = options.LocalComfyRoot,
            },
            options.DataDir);

        // [段3e] 本地生成（RVC 变声）——推理代码随 ComfyDock 走，模型住 ComfyUI\models\RVC，跑在 python_embeded 上
        var rvc = new RvcService(
            new RvcConfig
            {
                PythonPath = options.RvcPython,
                RepoDir = options.RvcRepo,
                ModelsRoot = options.RvcModels,
            },
            options.DataDir,
            options.RvcOutDir);

        // 实时链 worker 脚本（线 2 产出）——启动时注入源码，首次开流落到 data\rvc\rvc_realtime_worker.py
        rvc.SetRealtimeWorkerSource(RvcRealtimeWorkerScript.Source);

        // [段3f] 本地生成（Qwen3-Omni 转写）——引擎是 llama.cpp 的 llama-mtmd-cli，模型住 ComfyUI\models\LLM
        var omni = new OmniService(
            new OmniConfig
            {
                CliPath = options.OmniCli,
                ModelPath = options.OmniModel,
                MmprojPath = options.OmniMmproj,
                PythonPath = options.OmniPython,
            },
            options.DataDir,
            options.OmniOutDir);

        // [段3g] 本地生成（VITS · MoeGoe 血统）——语音合成 / 语音转换；模型住 ComfyUI\models\VITS，推理借 python_embeded
        var vits = new VitsService(
            new VitsConfig
            {
                PythonPath = options.VitsPython,
                ScriptPath = options.VitsScript,
                ModelsRoot = options.VitsModels,
            },
            options.DataDir,
            options.VitsOutDir);

        // 桥接 VITS-VC：RVC 实时链的模型 / 说话人取自 VITS 页签（单一真相源——不另存一份）
        rvc.SetVitsBridgeSource(() => vits.BridgeSpec());

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:" + options.Port);

        WebApplication app = builder.Build();
        MapEndpoints(app, options, store, service, registry, shell, hub, gateway, local, rh, moss, tts, tagger, rvc, omni, vits);

        await app.StartAsync().ConfigureAwait(false);

        string panel = "http://127.0.0.1:" + options.Port + "/";
        Console.WriteLine("ComfyDock 面板：" + panel);
        Console.WriteLine("快捷方式目录：" + options.GoogleRoot);
        Console.WriteLine("快照文件：" + options.SnapshotPath);

        if (!options.NoBrowser)
        {
            TryOpenBrowser(panel);
        }

        await app.WaitForShutdownAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>注册全部 HTTP 端点——面板数据面 + 采集 / 工作流 / 打开 / 接管 / 诊断。</summary>
    /// <param name="app">Web 应用。</param>
    /// <param name="options">运行选项。</param>
    /// <param name="store">快照存储。</param>
    /// <param name="service">采集服务（含任务队列）。</param>
    /// <param name="registry">内置账号表。</param>
    /// <param name="shell">套壳浏览器客户端。</param>
    /// <param name="hub">受控浏览器中心。</param>
    /// <param name="gateway">CDP 会话入口。</param>
    /// <param name="local">本地 ComfyUI 入口。</param>
    /// <param name="rh">线上识别服务。</param>
    /// <param name="moss">本地生成（MOSS-Image）服务。</param>
    /// <param name="tts">本地生成（IrodoriTTS）服务。</param>
    /// <param name="tagger">本地生成（WD Tagger 打标）服务。</param>
    /// <param name="rvc">本地生成（RVC 变声）服务。</param>
    /// <param name="omni">本地生成（Qwen3-Omni 转写）服务。</param>
    /// <param name="vits">本地生成（VITS 语音合成 / 语音转换）服务。</param>
    private static void MapEndpoints(
        WebApplication app,
        AppOptions options,
        AccountStore store,
        CollectService service,
        AccountRegistryStore registry,
        ShellClient shell,
        BrowserHub hub,
        CdpGateway gateway,
        LocalComfy local,
        RhService rh,
        MossService moss,
        TtsService tts,
        TaggerService tagger,
        RvcService rvc,
        OmniService omni,
        VitsService vits)
    {
        string webRoot = Path.Combine(app.Environment.ContentRootPath, "Web", "wwwroot");
        if (!Directory.Exists(webRoot))
        {
            webRoot = Path.Combine(AppContext.BaseDirectory, "Web", "wwwroot");
        }

        if (Directory.Exists(webRoot))
        {
            var provider = new PhysicalFileProvider(webRoot);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
        }
        else
        {
            Console.Error.WriteLine("面板静态目录缺失：" + webRoot);
        }

        app.MapGet("/api/local-comfy", async (CancellationToken ct) =>
        {
            LocalComfyStatus status = await local.ProbeAsync(ct).ConfigureAwait(false);
            return Results.Json(new
            {
                running = status.Running,
                url = status.Url,
                checkedAt = status.CheckedAt,
                message = status.Message,
            }, JsonOptions);
        });

        app.MapPost("/api/local-comfy/start", async (CancellationToken ct) =>
        {
            LocalComfyStatus status = await local.LaunchAsync(120, ct).ConfigureAwait(false);
            if (status.Running)
            {
                local.OpenUi();
            }

            return Results.Json(new
            {
                running = status.Running,
                url = status.Url,
                checkedAt = status.CheckedAt,
                message = status.Message,
            }, JsonOptions);
        });

        app.MapGet("/api/accounts", () => Results.Json(service.Snapshot, JsonOptions));

        // 额度概览——合计推测剩余 + 最早刷新时刻（外部探针 / 领航员健康检查用）
        app.MapGet("/api/summary", () => Results.Json(rh.Summary(), JsonOptions));

        app.MapGet("/api/options", () => Results.Json(new
        {
            googleRoot = options.GoogleRoot,
            registryPath = registry.FilePath,
            shellPath = options.ShellPath,
            snapshotPath = options.SnapshotPath,
            url = options.Url,
            workspaceUrl = options.WorkspaceUrl,
            port = options.Port,
        }, JsonOptions));

        // 换版自证——面板显示本实例版本与启动时刻；换版位上的文件比本实例新即提示可以重启换版
        app.MapGet("/api/app/version", () => Results.Json(AppControl.Describe(), JsonOptions));

        app.MapPost("/api/collect", () =>
        {
            TaskRecord item = service.EnqueueAll();
            return Results.Json(new { queued = true, id = item.Id }, JsonOptions);
        });

        app.MapPost("/api/collect/one", (CollectOneRequest request) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.ShortcutPath))
            {
                return Results.Json(new { queued = false, reason = "shortcutPath 为空" }, JsonOptions, statusCode: 400);
            }

            TaskRecord? item = service.EnqueueOne(request.ShortcutPath, out string reason);
            if (item is null)
            {
                return Results.Json(new { queued = false, reason }, JsonOptions, statusCode: 404);
            }

            return Results.Json(new { queued = true, id = item.Id }, JsonOptions);
        });

        app.MapGet("/api/collect/status", () => Results.Json(service.Progress, JsonOptions));

        app.MapGet("/api/tasks", () => Results.Json(service.Tasks.Snapshot(), JsonOptions));

        // 面板电源控制——重启（起新实例，本进程退出）/ 关闭（只停本程序，不动浏览器实例）
        app.MapPost("/api/app/restart", (IHostApplicationLifetime lifetime) =>
        {
            if (!AppControl.TryStartNewInstance(out string message))
            {
                return Results.Json(new { ok = false, error = message }, JsonOptions, statusCode: 500);
            }

            _ = AppControl.ScheduleStopAsync(lifetime);
            return Results.Json(new { ok = true, message }, JsonOptions);
        });

        app.MapPost("/api/app/shutdown", (IHostApplicationLifetime lifetime) =>
        {
            _ = AppControl.ScheduleStopAsync(lifetime);
            return Results.Json(new { ok = true, message = "面板正在关闭" }, JsonOptions);
        });

        app.MapPost("/api/open-url", async (OpenUrlRequest request) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.ShortcutPath) || string.IsNullOrWhiteSpace(request.Url))
            {
                return Results.Json(new { ok = false, error = "参数不完整" }, JsonOptions, statusCode: 400);
            }

            ShortcutEntry? target = FindTarget(registry, request.ShortcutPath);
            if (target is null)
            {
                return Results.Json(new { ok = false, error = "账号不在账号表内" }, JsonOptions, statusCode: 404);
            }

            // [段1] 套壳浏览器前台打开——同一个浏览器既是登录窗口也是采集通道，登录态就地保存
            try
            {
                JsonElement response = await shell.OpenAsync(target.ShortcutPath, request.Url, true, 90000).ConfigureAwait(false);
                if (!ShellClient.IsOk(response))
                {
                    return Results.Json(new { ok = false, error = ShellClient.ErrorText(response) }, JsonOptions, statusCode: 500);
                }

                return Results.Json(new { ok = true, via = "shell" }, JsonOptions);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, JsonOptions, statusCode: 500);
            }
        });

        app.MapPost("/api/browser/takeover", async (OpenUrlRequest request) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.ShortcutPath))
            {
                return Results.Json(new { ok = false, error = "参数不完整" }, JsonOptions, statusCode: 400);
            }

            ShortcutEntry? target = FindTarget(registry, request.ShortcutPath);
            if (target is null)
            {
                return Results.Json(new { ok = false, error = "账号不在账号表内" }, JsonOptions, statusCode: 404);
            }

            string url = options.Url;
            if (!string.IsNullOrWhiteSpace(request.Url))
            {
                url = request.Url;
            }

            // [段1] 接管语义并入套壳——账号一律由套壳承载，不再有「面板之外的窗口」要结束
            try
            {
                JsonElement response = await shell.OpenAsync(target.ShortcutPath, url, true, 90000).ConfigureAwait(false);
                if (!ShellClient.IsOk(response))
                {
                    return Results.Json(new { ok = false, error = ShellClient.ErrorText(response) }, JsonOptions, statusCode: 500);
                }

                return Results.Json(new { ok = true, killed = 0, launched = true, via = "shell" }, JsonOptions);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = "接管失败：" + ex.Message }, JsonOptions, statusCode: 500);
            }
        });

        app.MapGet("/api/local-workflows", () =>
        {
            string root = options.LocalWorkflowsRoot;
            if (!Directory.Exists(root))
            {
                return Results.Json(new
                {
                    ok = false,
                    error = "本地工作流目录不存在：" + root,
                    root,
                    count = 0,
                    items = Array.Empty<object>(),
                }, JsonOptions);
            }

            var items = new List<object>();
            foreach (string file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                string rel = Path.GetRelativePath(root, file);
                string? dir = Path.GetDirectoryName(rel);
                if (dir is null)
                {
                    dir = "";
                }

                items.Add(new
                {
                    name = Path.GetFileNameWithoutExtension(file),
                    file = Path.GetFileName(file),
                    rel,
                    dir,
                    full = file,
                    size = info.Length,
                    modified = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                });
            }

            return Results.Json(new
            {
                ok = true,
                error = "",
                root,
                count = items.Count,
                items,
            }, JsonOptions);
        });

        app.MapPost("/api/workflow/delete", (DeleteWorkflowRequest request) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.ShortcutPath) || string.IsNullOrWhiteSpace(request.WorkflowId))
            {
                return Results.Json(new { queued = false, reason = "参数不完整" }, JsonOptions, statusCode: 400);
            }

            ShortcutEntry? target = FindTarget(registry, request.ShortcutPath);
            if (target is null)
            {
                return Results.Json(new { queued = false, reason = "快捷方式不在扫描目录内" }, JsonOptions, statusCode: 404);
            }

            string workflowId = request.WorkflowId;
            string profileDir = target.ProfileDir;
            string label = "删除工作流 " + workflowId;
            if (!string.IsNullOrWhiteSpace(request.Name))
            {
                label = "删除 " + request.Name;
            }

            string shortcutPath = target.ShortcutPath;
            TaskRecord item = service.Tasks.Enqueue("delete", label, shortcutPath, () => RunDeleteAsync(shell, options, service, shortcutPath, workflowId));
            return Results.Json(new { queued = true, id = item.Id }, JsonOptions);
        });

        app.MapPost("/api/workflow/upload", (UploadWorkflowRequest request) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.ShortcutPath) || string.IsNullOrWhiteSpace(request.FilePath))
            {
                return Results.Json(new { queued = false, reason = "参数不完整" }, JsonOptions, statusCode: 400);
            }

            ShortcutEntry? target = FindTarget(registry, request.ShortcutPath);
            if (target is null)
            {
                return Results.Json(new { queued = false, reason = "快捷方式不在扫描目录内" }, JsonOptions, statusCode: 404);
            }

            string filePath = request.FilePath;
            string profileDir = target.ProfileDir;
            string label = "上传 " + Path.GetFileName(filePath);
            TaskRecord item = service.Tasks.Enqueue("upload", label, target.ShortcutPath, () => RunUploadAsync(shell, options, target.ShortcutPath, filePath));
            return Results.Json(new { queued = true, id = item.Id }, JsonOptions);
        });

        app.MapPost("/api/diag/eval", async (EvalRequest request, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.ShortcutPath) || string.IsNullOrWhiteSpace(request.Script))
            {
                return Results.Json(new { ok = false, error = "参数不完整" }, JsonOptions, statusCode: 400);
            }

            ShortcutEntry? target = FindTarget(registry, request.ShortcutPath);

            if (target is null)
            {
                return Results.Json(new { ok = false, error = "快捷方式不在扫描目录内" }, JsonOptions, statusCode: 404);
            }

            await using CdpLease lease = await gateway.AcquireTemporaryAsync(target.ProfileDir, ct).ConfigureAwait(false);
            CdpSession session = lease.Session;
            await session.EnablePageAsync(ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(request.Url))
            {
                await session.NavigateAsync(request.Url, ct).ConfigureAwait(false);
                await Task.Delay(request.WaitMs > 0 ? request.WaitMs : 6000, ct).ConfigureAwait(false);
            }

            string output = await session.EvaluateAsync(request.Script, request.AwaitPromise, ct).ConfigureAwait(false);
            return Results.Json(new { ok = true, output }, JsonOptions);
        });

        app.MapPost("/api/diag/probe", async (ProbeRequest request, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.ShortcutPath) || string.IsNullOrWhiteSpace(request.Url))
            {
                return Results.Json(new { ok = false, error = "参数不完整" }, JsonOptions, statusCode: 400);
            }

            ShortcutEntry? target = FindTarget(registry, request.ShortcutPath);

            if (target is null)
            {
                return Results.Json(new { ok = false, error = "快捷方式不在扫描目录内" }, JsonOptions, statusCode: 404);
            }

            await using CdpLease lease = await gateway.AcquireTemporaryAsync(target.ProfileDir, ct).ConfigureAwait(false);
            CdpSession session = lease.Session;
            await session.EnablePageAsync(ct).ConfigureAwait(false);
            await session.NavigateAsync(request.Url, ct).ConfigureAwait(false);
            await Task.Delay(8000, ct).ConfigureAwait(false);

            string href = await session.EvaluateAsync("location.href", false, ct).ConfigureAwait(false);
            string title = await session.EvaluateAsync("document.title", false, ct).ConfigureAwait(false);
            string textLength = await session.EvaluateAsync("document.body ? document.body.innerText.length : 0", false, ct).ConfigureAwait(false);
            string sample = await session.EvaluateAsync("document.body ? document.body.innerText.slice(0, 400) : ''", false, ct).ConfigureAwait(false);

            return Results.Json(new
            {
                ok = true,
                href,
                title,
                textLength,
                sample,
            }, JsonOptions);
        });

        app.MapPost("/api/open", async (OpenRequest request) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.ShortcutPath))
            {
                return Results.Json(new { ok = false, error = "账号 id 为空" }, JsonOptions, statusCode: 400);
            }

            ShortcutEntry? target = FindTarget(registry, request.ShortcutPath);
            if (target is null)
            {
                return Results.Json(new { ok = false, error = "账号不在账号表内" }, JsonOptions, statusCode: 404);
            }

            // [段1] 套壳浏览器打开——同一个浏览器既是登录窗口也是采集通道，登录态就地保存
            try
            {
                JsonElement response = await shell.OpenAsync(target.ShortcutPath, options.Url, true, 90000).ConfigureAwait(false);
                if (!ShellClient.IsOk(response))
                {
                    return Results.Json(new { ok = false, error = ShellClient.ErrorText(response) }, JsonOptions, statusCode: 500);
                }

                return Results.Json(new { ok = true, via = "shell" }, JsonOptions);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, JsonOptions, statusCode: 500);
            }
        });

        // 账号表（内置账号源）——面板增删改走这里
        app.MapGet("/api/accounts/registry", () =>
        {
            List<object> items = new List<object>();
            foreach (AccountEntry entry in registry.All())
            {
                items.Add(new
                {
                    id = entry.Id,
                    name = entry.Name,
                    profileDir = registry.ResolveProfileDir(entry),
                    importFrom = entry.ImportFrom,
                    enabled = entry.Enabled,
                    note = entry.Note,
                });
            }

            return Results.Json(new { ok = true, accounts = items, file = registry.FilePath }, JsonOptions);
        });

        app.MapPost("/api/accounts/add", (AddAccountRequest request) =>
        {
            if (request is null)
            {
                return Results.Json(new { ok = false, error = "参数为空" }, JsonOptions, statusCode: 400);
            }

            string error;
            AccountEntry? created = registry.Add(request.Name ?? "", request.Note ?? "", request.ImportFrom ?? "", out error);
            if (created == null)
            {
                return Results.Json(new { ok = false, error }, JsonOptions, statusCode: 400);
            }

            service.SyncWithRegistry();
            return Results.Json(new { ok = true, id = created.Id }, JsonOptions);
        });

        app.MapPost("/api/accounts/update", (UpdateAccountRequest request) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.AccountId))
            {
                return Results.Json(new { ok = false, error = "accountId 为空" }, JsonOptions, statusCode: 400);
            }

            string error;
            AccountEntry? updated = registry.Update(request.AccountId, request.Name, request.Note, request.Enabled, out error);
            if (updated == null)
            {
                return Results.Json(new { ok = false, error }, JsonOptions, statusCode: 404);
            }

            service.SyncWithRegistry();
            return Results.Json(new { ok = true, id = updated.Id, enabled = updated.Enabled }, JsonOptions);
        });

        app.MapPost("/api/accounts/remove", async (RemoveAccountRequest request) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.AccountId))
            {
                return Results.Json(new { ok = false, error = "accountId 为空" }, JsonOptions, statusCode: 400);
            }

            AccountEntry? entry = registry.Find(request.AccountId);
            if (entry == null)
            {
                return Results.Json(new { ok = false, error = "账号不存在" }, JsonOptions, statusCode: 404);
            }

            if (request.DeleteProfile)
            {
                try
                {
                    await shell.RemoveAsync(request.AccountId, true).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return Results.Json(new { ok = false, error = "删除浏览器数据失败：" + ex.Message }, JsonOptions, statusCode: 500);
                }
            }

            bool removed = registry.Remove(request.AccountId);
            service.SyncWithRegistry();
            return Results.Json(new { ok = removed }, JsonOptions);
        });

        app.MapPost("/api/shell/show", async () =>
        {
            try
            {
                JsonElement response = await shell.ShowAsync().ConfigureAwait(false);
                return Results.Json(new { ok = ShellClient.IsOk(response) }, JsonOptions);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, JsonOptions, statusCode: 500);
            }
        });

        // 线上识别（RunningHub）——账号复用 accounts.json，面板不展示密钥
        app.MapGet("/api/rh", () => Results.Json(rh.Snapshot(), JsonOptions));

        app.MapPost("/api/rh/config", (RhConfigRequest request) =>
        {
            if (request is null)
            {
                return Results.Json(new { ok = false, error = "参数不完整" }, JsonOptions, statusCode: 400);
            }

            string workspaceId = request.WorkspaceId ?? "";
            if (workspaceId.Length == 0)
            {
                return Results.Json(new { ok = false, error = "缺 workspaceId" }, JsonOptions, statusCode: 400);
            }

            RhWorkspace? workspace = rh.FindWorkspaceOrNull(workspaceId);
            if (workspace is null)
            {
                return Results.Json(new { ok = false, error = "没有这个工作区：" + workspaceId }, JsonOptions, statusCode: 404);
            }

            // 按工作区类型分流——生成块改图 + 文，音频块改音频源，互不打扰
            if (workspace.Kind == RhWorkspace.KindGen)
            {
                if (request.ImageFile is not null || request.Prompt is not null)
                {
                    workspace = rh.UpdateWorkspaceGen(workspaceId, request.ImageFile, request.Prompt);
                }
            }
            else if (request.AudioFile is not null || request.AudioDir is not null)
            {
                workspace = rh.UpdateWorkspaceAudio(workspaceId, request.AudioFile, request.AudioDir);
            }

            if (request.AutoPush.HasValue)
            {
                workspace = rh.SetWorkspaceAutoPush(workspaceId, request.AutoPush.Value);
            }

            if (!string.IsNullOrEmpty(request.Name) || !string.IsNullOrEmpty(request.WorkflowId))
            {
                workspace = rh.SetWorkspaceWorkflow(workspaceId, request.Name, request.WorkflowId);
            }

            return Results.Json(new { ok = true, workspace }, JsonOptions);
        });

        app.MapPost("/api/rh/workspace/add", (RhWorkspaceAddRequest request) =>
        {
            string name = "";
            if (request is not null && request.Name is not null)
            {
                name = request.Name.Trim();
            }

            if (name.Length == 0)
            {
                return Results.Json(new { ok = false, error = "缺工作区名" }, JsonOptions, statusCode: 400);
            }

            string workflowId = "";
            if (request is not null && request.WorkflowId is not null)
            {
                workflowId = request.WorkflowId.Trim();
            }

            RhWorkspace? workspace = rh.AddWorkspace(name, workflowId);
            if (workspace is null)
            {
                return Results.Json(new { ok = false, error = "工作区名重复或非法：" + name }, JsonOptions, statusCode: 400);
            }

            return Results.Json(new { ok = true, workspace }, JsonOptions);
        });

        app.MapPost("/api/rh/workspace/remove", (RhWorkspaceRemoveRequest request) =>
        {
            string workspaceId = "";
            if (request is not null && request.WorkspaceId is not null)
            {
                workspaceId = request.WorkspaceId;
            }

            if (workspaceId.Length == 0)
            {
                return Results.Json(new { ok = false, error = "缺 workspaceId" }, JsonOptions, statusCode: 400);
            }

            string message = rh.RemoveWorkspace(workspaceId);
            bool ok = !message.StartsWith("没有这个工作区", StringComparison.Ordinal)
                && !message.StartsWith("内置工作区", StringComparison.Ordinal);
            return Results.Json(new { ok, message }, JsonOptions, statusCode: ok ? 200 : 400);
        });

        app.MapPost("/api/rh/pick", async (RhPickRequest request) =>
        {
            string kind = "";
            if (request is not null && request.Kind is not null)
            {
                kind = request.Kind;
            }

            bool directory = string.Equals(kind, "dir", StringComparison.OrdinalIgnoreCase);
            bool image = string.Equals(kind, "image", StringComparison.OrdinalIgnoreCase);
            if (!directory && !image && !string.Equals(kind, "file", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(new { ok = false, error = "kind 取 file / image / dir" }, JsonOptions, statusCode: 400);
            }

            try
            {
                // 等待走 Task——对话框开着时不占 Kestrel 请求线程（上限 300 秒，超时自动关框并出声）
                string path;
                if (directory)
                {
                    path = await NativePicker.PickDirectoryAsync("选择音频目录").ConfigureAwait(false);
                }
                else if (image)
                {
                    path = await NativePicker.PickImageFileAsync("选择图片").ConfigureAwait(false);
                }
                else
                {
                    path = await NativePicker.PickAudioFileAsync("选择音频文件").ConfigureAwait(false);
                }

                return Results.Json(new { ok = true, path }, JsonOptions);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, JsonOptions, statusCode: 500);
            }
        });

        app.MapPost("/api/rh/submit", async (RhSubmitRequest request, CancellationToken ct) =>
        {
            string workspaceId = "";
            string path = "";
            string prompt = "";
            if (request is not null)
            {
                workspaceId = request.WorkspaceId ?? "";
                path = request.Path ?? "";
                prompt = request.Prompt ?? "";
            }

            if (workspaceId.Length == 0)
            {
                return Results.Json(new { ok = false, message = "缺 workspaceId" }, JsonOptions, statusCode: 400);
            }

            RhWorkspace? workspace = rh.FindWorkspaceOrNull(workspaceId);
            if (workspace is null)
            {
                return Results.Json(new { ok = false, message = "没有这个工作区：" + workspaceId }, JsonOptions, statusCode: 404);
            }

            // [段1] 生成块走「图 + 文」；音频块走音频——两条路互不打扰
            if (workspace.Kind == RhWorkspace.KindGen)
            {
                if (path.Length == 0)
                {
                    path = workspace.ImageFile;
                }

                if (prompt.Length == 0)
                {
                    prompt = workspace.Prompt;
                }

                if (path.Length == 0)
                {
                    return Results.Json(new { ok = false, message = "没有指定图片——先在该工作区选图片" }, JsonOptions, statusCode: 400);
                }

                RhSubmitResult genResult = await rh.SubmitGenAsync(workspaceId, path, prompt, ct).ConfigureAwait(false);
                return Results.Json(genResult, JsonOptions);
            }

            if (path.Length == 0 && workspace.AudioFile.Length > 0)
            {
                path = workspace.AudioFile;
            }

            if (path.Length == 0)
            {
                return Results.Json(new { ok = false, message = "没有指定音频——先在该工作区选音频或目录" }, JsonOptions, statusCode: 400);
            }

            RhSubmitResult result = await rh.SubmitAsync(workspaceId, path, ct).ConfigureAwait(false);
            return Results.Json(result, JsonOptions);
        });

        app.MapPost("/api/rh/poll", async (CancellationToken ct) =>
        {
            RhPollSummary summary = await rh.PollOnceAsync(ct).ConfigureAwait(false);
            return Results.Json(summary, JsonOptions);
        });

        app.MapPost("/api/rh/cancel", async (RhCancelRequest request, CancellationToken ct) =>
        {
            string id = "";
            if (request is not null && request.Id is not null)
            {
                id = request.Id;
            }

            if (id.Length == 0)
            {
                return Results.Json(new { ok = false, message = "缺 id" }, JsonOptions, statusCode: 400);
            }

            string message = await rh.CancelAsync(id, ct).ConfigureAwait(false);
            return Results.Json(new { ok = true, message }, JsonOptions);
        });

        // 本地生成（MOSS-Image）——能力板块的第三个页签：依赖索引 / 下载 / 插件 / 生成
        app.MapGet("/api/moss", async (CancellationToken ct) =>
        {
            return Results.Json(await moss.SnapshotAsync(ct).ConfigureAwait(false), JsonOptions);
        });

        app.MapGet("/api/moss/live", () =>
        {
            // 轻量轮询面——只回任务 / 下载 / 插件状态，不重探环境（nvidia-smi 不进热路径）
            return Results.Json(new
            {
                job = moss.Job(),
                downloads = moss.Downloads(),
                plugin = moss.Plugin,
                local = moss.Local,
            }, JsonOptions);
        });

        app.MapPost("/api/moss/config", (MossConfig request) =>
        {
            if (request is null)
            {
                return Results.Json(new { ok = false, error = "参数不完整" }, JsonOptions, statusCode: 400);
            }

            MossConfig saved = moss.UpdateConfig(request);
            return Results.Json(new { ok = true, config = saved }, JsonOptions);
        });

        app.MapPost("/api/moss/download", (MossDownloadRequest request) =>
        {
            string plan = request?.Plan ?? "";
            string fileId = request?.FileId ?? "";
            if (plan.Length > 0)
            {
                (int queued, System.Collections.Generic.List<string> notes) = moss.DownloadPlan(plan);
                return Results.Json(new { ok = queued > 0, queued, notes }, JsonOptions);
            }

            if (fileId.Length > 0)
            {
                (bool ok, string note) = moss.DownloadFile(fileId);
                return Results.Json(new { ok, queued = ok ? 1 : 0, notes = new[] { note } }, JsonOptions);
            }

            return Results.Json(new { ok = false, error = "缺 plan 或 fileId" }, JsonOptions, statusCode: 400);
        });

        app.MapPost("/api/moss/download/cancel", (MossFileRequest request) =>
        {
            string fileId = request?.FileId ?? "";
            if (fileId.Length == 0)
            {
                return Results.Json(new { ok = false, error = "缺 fileId" }, JsonOptions, statusCode: 400);
            }

            bool ok = moss.CancelDownload(fileId);
            return Results.Json(new { ok, message = ok ? "已请求取消" : "没有这条在途下载" }, JsonOptions);
        });

        app.MapPost("/api/moss/plugin/install", async (CancellationToken ct) =>
        {
            string message = await moss.InstallPluginAsync(ct).ConfigureAwait(false);
            return Results.Json(new { ok = moss.Plugin.State == "done", message, state = moss.Plugin.State }, JsonOptions);
        });

        app.MapPost("/api/moss/generate", async (CancellationToken ct) =>
        {
            (bool ok, string message) = await moss.GenerateAsync(ct).ConfigureAwait(false);
            return Results.Json(new { ok, message, job = moss.Job() }, JsonOptions);
        });

        app.MapPost("/api/moss/interrupt", async (CancellationToken ct) =>
        {
            string message = await moss.InterruptAsync(ct).ConfigureAwait(false);
            return Results.Json(new { ok = true, message }, JsonOptions);
        });

        app.MapPost("/api/moss/engine/restart", async (MossEngineRequest request, CancellationToken ct) =>
        {
            string mode = request?.Mode ?? "";
            (bool ok, string message) = await moss.RestartEngineAsync(mode, ct).ConfigureAwait(false);
            return Results.Json(new { ok, message }, JsonOptions);
        });

        app.MapPost("/api/moss/local/stop", () =>
        {
            string message = moss.StopLocal();
            return Results.Json(new { ok = true, message, local = moss.Local }, JsonOptions);
        });

        app.MapGet("/api/moss/image", (string? name) =>
        {
            string path = moss.OutputPath(name ?? "");
            if (path.Length == 0)
            {
                return Results.NotFound();
            }

            return Results.File(path, "image/png");
        });

        app.MapPost("/api/moss/open-out", () =>
        {
            try
            {
                Directory.CreateDirectory(moss.OutDir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = moss.OutDir,
                    UseShellExecute = true,
                });
                return Results.Json(new { ok = true, path = moss.OutDir }, JsonOptions);
            }
            catch (Exception ex) when (ex is IOException || ex is System.ComponentModel.Win32Exception)
            {
                return Results.Json(new { ok = false, error = ex.Message }, JsonOptions, statusCode: 500);
            }
        });

        // 本地生成（IrodoriTTS）——能力板块第三个页签里的第二个生成器：环境 / 配置 / 合成 / 产物
        app.MapGet("/api/tts", async (CancellationToken ct) =>
        {
            return Results.Json(await tts.SnapshotAsync(ct).ConfigureAwait(false), JsonOptions);
        });

        app.MapGet("/api/tts/live", () =>
        {
            // 轻量轮询面——只回任务与引擎状态，不重探磁盘
            return Results.Json(new
            {
                job = tts.Job(),
                engine = tts.Engine,
            }, JsonOptions);
        });

        app.MapPost("/api/tts/config", (TtsConfig request) =>
        {
            if (request is null)
            {
                return Results.Json(new { ok = false, error = "参数不完整" }, JsonOptions, statusCode: 400);
            }

            TtsConfig saved = tts.UpdateConfig(request);
            return Results.Json(new { ok = true, config = saved }, JsonOptions);
        });

        app.MapPost("/api/tts/generate", async (CancellationToken ct) =>
        {
            (bool ok, string message) = await tts.GenerateAsync(ct).ConfigureAwait(false);
            return Results.Json(new { ok, message, job = tts.Job() }, JsonOptions);
        });

        app.MapPost("/api/tts/stop", () =>
        {
            string message = tts.Stop();
            return Results.Json(new { ok = true, message, engine = tts.Engine, job = tts.Job() }, JsonOptions);
        });

        app.MapGet("/api/tts/audio", (string? name) =>
        {
            string path = tts.OutputPath(name ?? "");
            if (path.Length == 0)
            {
                return Results.NotFound();
            }

            return Results.File(path, "audio/wav");
        });

        app.MapPost("/api/tts/open-out", () =>
        {
            try
            {
                Directory.CreateDirectory(tts.OutDir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = tts.OutDir,
                    UseShellExecute = true,
                });
                return Results.Json(new { ok = true, path = tts.OutDir }, JsonOptions);
            }
            catch (Exception ex) when (ex is IOException || ex is System.ComponentModel.Win32Exception)
            {
                return Results.Json(new { ok = false, error = ex.Message }, JsonOptions, statusCode: 500);
            }
        });

        // 本地生成（VITS · MoeGoe 血统）——语音合成 / 语音转换：模型清单 / 加载 / 清理预览 / 合成 / 换音色 / 产物
        app.MapGet("/api/vits", async (CancellationToken ct) =>
        {
            return Results.Json(await vits.SnapshotAsync(ct).ConfigureAwait(false), JsonOptions);
        });

        app.MapGet("/api/vits/live", () =>
        {
            // 轻量轮询面——只回任务与引擎状态，不重扫磁盘
            return Results.Json(new
            {
                job = vits.Job(),
                engine = vits.Engine,
            }, JsonOptions);
        });

        app.MapPost("/api/vits/config", async (VitsConfig request, CancellationToken ct) =>
        {
            if (request is null)
            {
                return Results.Json(new { ok = false, error = "参数不完整" }, JsonOptions, statusCode: 400);
            }

            VitsConfig saved = vits.UpdateConfig(request);
            (bool bridgeOk, string bridgeMessage) = await rvc
                .RealtimeRefreshBridgeAsync(ct).ConfigureAwait(false);
            return Results.Json(new
            {
                ok = true,
                config = saved,
                bridge = new { ok = bridgeOk, message = bridgeMessage },
            }, JsonOptions);
        });

        app.MapPost("/api/vits/load", async (string? model, CancellationToken ct) =>
        {
            (bool ok, string message) = await vits.LoadAsync(model ?? "", ct).ConfigureAwait(false);
            string bridgeMessage = "";
            if (ok)
            {
                (bool _, string note) = await rvc.RealtimeRefreshBridgeAsync(ct).ConfigureAwait(false);
                bridgeMessage = note;
            }

            return Results.Json(new { ok, message, bridgeMessage, engine = vits.Engine }, JsonOptions);
        });

        app.MapPost("/api/vits/clean", async (VitsTextRequest? request, CancellationToken ct) =>
        {
            (string cleaned, string error) = await vits.CleanAsync(request?.Text ?? "", ct).ConfigureAwait(false);
            return Results.Json(new { ok = error.Length == 0, cleaned, error, engine = vits.Engine }, JsonOptions);
        });

        app.MapPost("/api/vits/tts", async (CancellationToken ct) =>
        {
            (bool ok, string message) = await vits.TtsAsync(ct).ConfigureAwait(false);
            return Results.Json(new { ok, message, job = vits.Job(), engine = vits.Engine }, JsonOptions);
        });

        app.MapPost("/api/vits/tts-batch", async (VitsBatchRequest? request, CancellationToken ct) =>
        {
            // 一次调用跑完整批勾选的说话人——worker 内共用一次模型加载与一次文本前端，前端只等一个回执
            (bool ok, string message, VitsBatchColumn? batch) = await vits
                .TtsBatchAsync(request?.Sids ?? new List<int>(), ct).ConfigureAwait(false);
            return Results.Json(new { ok, message, batch, job = vits.Job(), engine = vits.Engine }, JsonOptions);
        });

        app.MapPost("/api/vits/vc", async (CancellationToken ct) =>
        {
            (bool ok, string message) = await vits.VcAsync(ct).ConfigureAwait(false);
            return Results.Json(new { ok, message, job = vits.Job(), engine = vits.Engine }, JsonOptions);
        });

        app.MapPost("/api/vits/stop", () =>
        {
            string message = vits.Stop();
            return Results.Json(new { ok = true, message, engine = vits.Engine, job = vits.Job() }, JsonOptions);
        });

        app.MapGet("/api/vits/audio", (string? name) =>
        {
            string path = vits.OutputPath(name ?? "");
            if (path.Length == 0)
            {
                return Results.NotFound();
            }

            return Results.File(path, "audio/wav");
        });

        app.MapPost("/api/vits/open-out", () =>
        {
            try
            {
                Directory.CreateDirectory(vits.OutDir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = vits.OutDir,
                    UseShellExecute = true,
                });
                return Results.Json(new { ok = true, path = vits.OutDir }, JsonOptions);
            }
            catch (Exception ex) when (ex is IOException || ex is System.ComponentModel.Win32Exception)
            {
                return Results.Json(new { ok = false, error = ex.Message }, JsonOptions, statusCode: 500);
            }
        });

        // 本地生成（WD Tagger 打标）——能力板块第三个页签里的第三个生成器：环境 / 模型索引与下载 / 打标 / 结果
        app.MapGet("/api/tagger", async (CancellationToken ct) =>
        {
            return Results.Json(await tagger.SnapshotAsync(ct).ConfigureAwait(false), JsonOptions);
        });

        app.MapGet("/api/tagger/live", () =>
        {
            // 轻量轮询面——只回任务、引擎与下载，不重探磁盘
            return Results.Json(new
            {
                job = tagger.Job(),
                engine = tagger.Engine,
                downloads = tagger.Downloads(),
            }, JsonOptions);
        });

        app.MapPost("/api/tagger/config", (TaggerConfig request) =>
        {
            if (request is null)
            {
                return Results.Json(new { ok = false, error = "参数不完整" }, JsonOptions, statusCode: 400);
            }

            TaggerConfig saved = tagger.UpdateConfig(request);
            return Results.Json(new { ok = true, config = saved }, JsonOptions);
        });

        app.MapPost("/api/tagger/pick", async (RhPickRequest request) =>
        {
            string kind = request is null || request.Kind is null ? "image" : request.Kind;
            bool directory = string.Equals(kind, "dir", StringComparison.OrdinalIgnoreCase);
            try
            {
                string path = directory
                    ? await NativePicker.PickDirectoryAsync("选择待打标目录").ConfigureAwait(false)
                    : await NativePicker.PickImageFileAsync("选择待打标图片").ConfigureAwait(false);
                return Results.Json(new { ok = true, path }, JsonOptions);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, JsonOptions, statusCode: 500);
            }
        });

        app.MapPost("/api/tagger/generate", async (CancellationToken ct) =>
        {
            (bool ok, string message) = await tagger.GenerateAsync(ct).ConfigureAwait(false);
            return Results.Json(new { ok, message, job = tagger.Job() }, JsonOptions);
        });

        app.MapPost("/api/tagger/stop", () =>
        {
            string message = tagger.Stop();
            return Results.Json(new { ok = true, message, engine = tagger.Engine, job = tagger.Job() }, JsonOptions);
        });

        app.MapPost("/api/tagger/download", (TaggerDownloadRequest request) =>
        {
            bool byModel = request is not null
                && string.Equals(request.Kind, "model", StringComparison.OrdinalIgnoreCase);
            if (byModel)
            {
                (bool okModel, string messageModel) = tagger.DownloadModel();
                return Results.Json(new { ok = okModel, message = messageModel }, JsonOptions);
            }

            if (request is null || string.IsNullOrWhiteSpace(request.Id))
            {
                return Results.Json(new { ok = false, error = "缺 id" }, JsonOptions, statusCode: 400);
            }

            (bool ok, string message) = tagger.DownloadFile(request.Id);
            return Results.Json(new { ok, message }, JsonOptions);
        });

        app.MapPost("/api/tagger/download/cancel", (TaggerDownloadRequest request) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Id))
            {
                return Results.Json(new { ok = false, error = "缺 id" }, JsonOptions, statusCode: 400);
            }

            string message = tagger.CancelDownload(request.Id);
            return Results.Json(new { ok = true, message }, JsonOptions);
        });

        app.MapGet("/api/tagger/text", (string? path) =>
        {
            string text = TaggerService.TextPathOf(path ?? "");
            if (text.Length == 0)
            {
                return Results.NotFound();
            }

            return Results.File(text, "text/plain; charset=utf-8");
        });

        app.MapGet("/api/tagger/image", (string? path) =>
        {
            // 只放行图片扩展名、且必须真实存在——不代理任意文件
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return Results.NotFound();
            }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            string mime = ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                ".gif" => "image/gif",
                ".tif" or ".tiff" => "image/tiff",
                _ => "",
            };

            if (mime.Length == 0)
            {
                return Results.NotFound();
            }

            return Results.File(path, mime);
        });

        app.MapPost("/api/tagger/open-out", (TaggerOpenRequest request) =>
        {
            string dir = request is null ? "" : (request.Path ?? "");
            if (dir.Length == 0 || !Directory.Exists(dir))
            {
                return Results.Json(new { ok = false, error = "目录不存在：" + dir }, JsonOptions, statusCode: 400);
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true,
                });
                return Results.Json(new { ok = true, path = dir }, JsonOptions);
            }
            catch (Exception ex) when (ex is IOException || ex is System.ComponentModel.Win32Exception)
            {
                return Results.Json(new { ok = false, error = ex.Message }, JsonOptions, statusCode: 500);
            }
        });

        // 本地生成（RVC 变声）——能力板块第三个页签里的第四个生成器：环境 / 音色模型 / 变声 / 产物
        app.MapGet("/api/rvc", async (CancellationToken ct) =>
        {
            return Results.Json(await rvc.SnapshotAsync(ct).ConfigureAwait(false), JsonOptions);
        });

        app.MapGet("/api/rvc/live", () =>
        {
            // 轻量轮询面——只回任务与引擎，不重探磁盘
            return Results.Json(new
            {
                job = rvc.Job(),
                engine = rvc.Engine,
            }, JsonOptions);
        });

        app.MapPost("/api/rvc/config", (RvcConfig request) =>
        {
            if (request is null)
            {
                return Results.Json(new { ok = false, error = "参数不完整" }, JsonOptions, statusCode: 400);
            }

            RvcConfig saved = rvc.UpdateConfig(request);
            return Results.Json(new { ok = true, config = saved }, JsonOptions);
        });

        app.MapPost("/api/rvc/pick", async (RhPickRequest request) =>
        {
            string kind = request is null || request.Kind is null ? "audio" : request.Kind;
            bool directory = string.Equals(kind, "dir", StringComparison.OrdinalIgnoreCase);
            try
            {
                string path = directory
                    ? await NativePicker.PickDirectoryAsync("选择待变声的目录").ConfigureAwait(false)
                    : await NativePicker.PickAudioFileAsync("选择待变声的音频").ConfigureAwait(false);
                return Results.Json(new { ok = true, path }, JsonOptions);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, JsonOptions, statusCode: 500);
            }
        });

        app.MapPost("/api/rvc/generate", async (CancellationToken ct) =>
        {
            (bool ok, string message) = await rvc.GenerateAsync(ct).ConfigureAwait(false);
            return Results.Json(new { ok, message, job = rvc.Job() }, JsonOptions);
        });

        app.MapPost("/api/rvc/stop", () =>
        {
            string message = rvc.Stop();
            return Results.Json(new { ok = true, message, engine = rvc.Engine, job = rvc.Job() }, JsonOptions);
        });

        app.MapGet("/api/rvc/audio", (string? name) =>
        {
            string path = rvc.OutputPath(name ?? "");
            if (path.Length == 0)
            {
                return Results.NotFound();
            }

            return Results.File(path, "audio/wav");
        });

        app.MapPost("/api/rvc/open-out", () =>
        {
            try
            {
                Directory.CreateDirectory(rvc.OutDir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = rvc.OutDir,
                    UseShellExecute = true,
                });
                return Results.Json(new { ok = true, path = rvc.OutDir }, JsonOptions);
            }
            catch (Exception ex) when (ex is IOException || ex is System.ComponentModel.Win32Exception)
            {
                return Results.Json(new { ok = false, error = ex.Message }, JsonOptions, statusCode: 500);
            }
        });

        // 实时变声（线 3）——RVC 页签「实时」子签：设备 / 开流 / 停流 / 热更 / 自检 / 送声驱动（VB-CABLE）
        app.MapGet("/api/rvc/realtime", async (CancellationToken ct) =>
        {
            return Results.Json(await rvc.RealtimeSnapshotAsync(ct).ConfigureAwait(false), JsonOptions);
        });

        app.MapGet("/api/rvc/realtime/live", () =>
        {
            // 轻量轮询面——只回实时状态与互斥位，不重探磁盘
            return Results.Json(new
            {
                state = rvc.Realtime,
                offlineBusy = rvc.OfflineBusy,
            }, JsonOptions);
        });

        app.MapPost("/api/rvc/realtime/start", async (RvcRealtimeConfig? request, CancellationToken ct) =>
        {
            (bool ok, string message) = await rvc.RealtimeStartAsync(request, ct).ConfigureAwait(false);
            return Results.Json(new { ok, message, state = rvc.Realtime }, JsonOptions);
        });

        app.MapPost("/api/rvc/realtime/stop", async (CancellationToken ct) =>
        {
            (bool ok, string message) = await rvc.RealtimeStopAsync(ct).ConfigureAwait(false);
            return Results.Json(new { ok, message, state = rvc.Realtime }, JsonOptions);
        });

        app.MapPost("/api/rvc/realtime/update", async (RvcRealtimeConfig? request, CancellationToken ct) =>
        {
            (bool ok, string message, List<string> applied, bool needRestart) = await rvc
                .RealtimeUpdateAsync(request, ct).ConfigureAwait(false);
            return Results.Json(new
            {
                ok,
                message,
                applied,
                needRestart,
                state = rvc.Realtime,
            }, JsonOptions);
        });

        app.MapPost("/api/rvc/realtime/devices", async (CancellationToken ct) =>
        {
            (bool ok, List<RvcHostApiItem> hostApis, string message) = await rvc
                .RealtimeDevicesAsync(ct).ConfigureAwait(false);
            return Results.Json(new { ok, message, hostApis, state = rvc.Realtime }, JsonOptions);
        });

        app.MapPost("/api/rvc/realtime/selftest", async (RvcRealtimeSelftestRequest? request, CancellationToken ct) =>
        {
            int seconds = 3;
            int outIndex = -1;
            int sampleRate = 48000;
            if (request != null)
            {
                seconds = request.Seconds;
                outIndex = request.OutIndex;
                sampleRate = request.SampleRate;
            }

            (bool ok, RvcRealtimeState result, string message) = await rvc
                .RealtimeSelftestAsync(seconds, outIndex, sampleRate, ct).ConfigureAwait(false);
            return Results.Json(new { ok, message, state = result }, JsonOptions);
        });

        app.MapGet("/api/rvc/realtime/vbcable", () =>
        {
            return Results.Json(new { ok = true, vbcable = rvc.VbCableDetect() }, JsonOptions);
        });

        app.MapPost("/api/rvc/realtime/vbcable/install", () =>
        {
            (bool ok, string message) = rvc.VbCableInstall();
            return Results.Json(new { ok, message, vbcable = rvc.VbCableDetect() }, JsonOptions);
        });

        // 本地生成（Qwen3-Omni 转写）——能力板块第三个页签里的第五个生成器：环境 / 提示词 / 转写 / 产物
        app.MapGet("/api/omni", async (CancellationToken ct) =>
        {
            return Results.Json(await omni.SnapshotAsync(ct).ConfigureAwait(false), JsonOptions);
        });

        app.MapGet("/api/omni/live", () =>
        {
            // 轻量轮询面——只回任务与引擎状态，不重探磁盘
            return Results.Json(new
            {
                job = omni.Job(),
                engine = omni.Engine,
            }, JsonOptions);
        });

        app.MapPost("/api/omni/config", (OmniConfig request) =>
        {
            if (request is null)
            {
                return Results.Json(new { ok = false, error = "参数不完整" }, JsonOptions, statusCode: 400);
            }

            OmniConfig saved = omni.UpdateConfig(request);
            return Results.Json(new { ok = true, config = saved }, JsonOptions);
        });

        app.MapPost("/api/omni/pick", async (RhPickRequest request) =>
        {
            string kind = request is null || request.Kind is null ? "media" : request.Kind;
            bool directory = string.Equals(kind, "dir", StringComparison.OrdinalIgnoreCase);
            try
            {
                string path = directory
                    ? await NativePicker.PickDirectoryAsync("选择待转写的目录").ConfigureAwait(false)
                    : await NativePicker.PickMediaFileAsync("选择待转写的音频或视频").ConfigureAwait(false);
                return Results.Json(new { ok = true, path }, JsonOptions);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, JsonOptions, statusCode: 500);
            }
        });

        app.MapPost("/api/omni/transcribe", async (CancellationToken ct) =>
        {
            (bool ok, string message) = await omni.GenerateAsync(ct).ConfigureAwait(false);
            return Results.Json(new { ok, message, job = omni.Job() }, JsonOptions);
        });

        app.MapPost("/api/omni/stop", () =>
        {
            string message = omni.Stop();
            return Results.Json(new { ok = true, message, engine = omni.Engine, job = omni.Job() }, JsonOptions);
        });

        app.MapGet("/api/omni/text", (string? name) =>
        {
            string path = omni.OutputPath(name ?? "");
            if (path.Length == 0)
            {
                return Results.NotFound();
            }

            return Results.File(path, "text/plain; charset=utf-8");
        });

        app.MapPost("/api/omni/open-out", () =>
        {
            try
            {
                Directory.CreateDirectory(omni.OutDir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = omni.OutDir,
                    UseShellExecute = true,
                });
                return Results.Json(new { ok = true, path = omni.OutDir }, JsonOptions);
            }
            catch (Exception ex) when (ex is IOException || ex is System.ComponentModel.Win32Exception)
            {
                return Results.Json(new { ok = false, error = ex.Message }, JsonOptions, statusCode: 500);
            }
        });
    }

    /// <summary>按账号 id 找账号槽位（内置账号表）。</summary>
    /// <param name="registry">账号表。</param>
    /// <param name="shortcutPath">账号 id。</param>
    /// <returns>槽位；找不到返回 null。</returns>
    private static ShortcutEntry? FindTarget(AccountRegistryStore registry, string shortcutPath)
    {
        AccountEntry? entry = registry.Find(shortcutPath);
        if (entry == null || !entry.Enabled)
        {
            return null;
        }

        return registry.ToEntry(entry);
    }

    /// <summary>受控打开地址——确保该账号有受控浏览器，再开一个前台标签页。</summary>
    /// <param name="hub">受控浏览器中心。</param>
    /// <param name="profileDir">浏览器用户数据目录。</param>
    /// <param name="url">目标地址。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>HTTP 结果。</returns>
    private static async Task<IResult> OpenTabResultAsync(BrowserHub hub, string profileDir, string url, CancellationToken ct)
    {
        try
        {
            BrowserOpenResult opened = await hub.EnsureAsync(profileDir, url, ct).ConfigureAwait(false);
            if (!opened.Launched)
            {
                await hub.OpenTabAsync(opened.Instance, url, ct).ConfigureAwait(false);
            }

            return Results.Json(new { ok = true }, JsonOptions);
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = "打开失败：" + ex.Message }, JsonOptions, statusCode: 500);
        }
    }

    /// <summary>任务体——删除一个工作流；站点删成功后同步从快照里摘掉该条。</summary>
    /// <param name="shell">套壳浏览器客户端。</param>
    /// <param name="options">运行选项。</param>
    /// <param name="service">采集服务（快照落点）。</param>
    /// <param name="accountId">账号 id。</param>
    /// <param name="workflowId">工作流 id。</param>
    /// <returns>任务结果。</returns>
    private static async Task<TaskOutcome> RunDeleteAsync(ShellClient shell, AppOptions options, CollectService service, string accountId, string workflowId)
    {
        var admin = new WorkflowAdmin(shell, options.WorkspaceUrl);
        WorkflowOpResult outcome = await admin.DeleteAsync(accountId, workflowId, CancellationToken.None).ConfigureAwait(false);

        // [段1] 站点删成功即从快照摘掉该条——面板立刻反映，不必等下一次采集
        if (outcome.Ok)
        {
            service.RemoveWorkflow(accountId, workflowId);
        }

        return BuildOutcome(outcome, "已删除", "删除失败");
    }

    /// <summary>任务体——上传一个工作流文件。</summary>
    /// <param name="shell">套壳浏览器客户端。</param>
    /// <param name="options">运行选项。</param>
    /// <param name="accountId">账号 id。</param>
    /// <param name="filePath">工作流 JSON 完整路径。</param>
    /// <returns>任务结果。</returns>
    private static async Task<TaskOutcome> RunUploadAsync(ShellClient shell, AppOptions options, string accountId, string filePath)
    {
        var admin = new WorkflowAdmin(shell, options.WorkspaceUrl);
        WorkflowOpResult outcome = await admin.UploadAsync(accountId, filePath, 45, CancellationToken.None).ConfigureAwait(false);
        return BuildOutcome(outcome, "已上传", "上传失败");
    }

    /// <summary>把站点返回折叠成任务结果（失败优先报原因）。</summary>
    /// <param name="outcome">站点操作结果。</param>
    /// <param name="okText">成功时的兜底文案。</param>
    /// <param name="failText">失败时的兜底文案。</param>
    /// <returns>任务结果。</returns>
    private static TaskOutcome BuildOutcome(WorkflowOpResult outcome, string okText, string failText)
    {
        string message = outcome.Message;
        if (!outcome.Ok && outcome.Error.Length > 0)
        {
            message = outcome.Error;
        }

        if (message.Length == 0)
        {
            if (outcome.Ok)
            {
                message = okText;
            }
            else
            {
                message = failText;
            }
        }

        return new TaskOutcome { Ok = outcome.Ok, Message = message };
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("自动打开浏览器失败：" + ex.Message);
        }
    }
}

/// <summary>打开快捷方式请求体。</summary>
/// <param name="ShortcutPath">快捷方式完整路径。</param>
public sealed record OpenRequest(string? ShortcutPath);

/// <summary>单行刷新请求体。</summary>
/// <param name="ShortcutPath">快捷方式完整路径。</param>
public sealed record CollectOneRequest(string? ShortcutPath);

/// <summary>页面探测请求体（诊断用）。</summary>
/// <param name="ShortcutPath">快捷方式完整路径。</param>
/// <param name="Url">要打开的地址。</param>
public sealed record ProbeRequest(string? ShortcutPath, string? Url);

/// <summary>用指定账号浏览器打开地址的请求体。</summary>
/// <param name="ShortcutPath">快捷方式完整路径。</param>
/// <param name="Url">要打开的地址。</param>
public sealed record OpenUrlRequest(string? ShortcutPath, string? Url);

/// <summary>新增账号请求体。</summary>
/// <param name="Name">显示名。</param>
/// <param name="Note">备注。</param>
/// <param name="ImportFrom">一次性导入源目录（旧 Chrome 用户目录）。</param>
public sealed record AddAccountRequest(string? Name, string? Note, string? ImportFrom);

/// <summary>改动账号请求体。</summary>
/// <param name="AccountId">账号 id。</param>
/// <param name="Name">新显示名（空 = 不改）。</param>
/// <param name="Note">新备注（null = 不改）。</param>
/// <param name="Enabled">启用状态（null = 不改）。</param>
public sealed record UpdateAccountRequest(string? AccountId, string? Name, string? Note, bool? Enabled);

/// <summary>删除账号请求体。</summary>
/// <param name="AccountId">账号 id。</param>
/// <param name="DeleteProfile">是否同时删除该账号的浏览器数据目录。</param>
public sealed record RemoveAccountRequest(string? AccountId, bool DeleteProfile);

/// <summary>页面内求值请求体（诊断用）。</summary>
/// <param name="ShortcutPath">快捷方式完整路径。</param>
/// <param name="Url">先导航到的地址。</param>
/// <param name="Script">要执行的 JS 表达式。</param>
/// <param name="AwaitPromise">是否等待 Promise。</param>
/// <param name="WaitMs">导航后等待毫秒数。</param>
public sealed record EvalRequest(string? ShortcutPath, string? Url, string? Script, bool AwaitPromise, int WaitMs);

/// <summary>上传工作流请求体。</summary>
/// <param name="ShortcutPath">快捷方式完整路径。</param>
/// <param name="FilePath">工作流 JSON 完整路径。</param>
public sealed record UploadWorkflowRequest(string? ShortcutPath, string? FilePath);

/// <summary>删除工作流请求体。</summary>
/// <param name="ShortcutPath">快捷方式完整路径。</param>
/// <param name="WorkflowId">工作流 id。</param>
/// <param name="Name">工作流名称（仅用于面板与任务说明）。</param>
public sealed record DeleteWorkflowRequest(string? ShortcutPath, string? WorkflowId, string? Name);

/// <summary>线上识别配置请求体——未提交的字段传 null 表示不改。</summary>
/// <param name="WorkspaceId">工作区 id。</param>
/// <param name="AudioFile">该工作区选中的音频文件完整路径。</param>
/// <param name="AudioDir">该工作区选中的音频目录。</param>
/// <param name="ImageFile">生成工作区选中的图片完整路径。</param>
/// <param name="Prompt">生成工作区的提示词。</param>
/// <param name="AutoPush">该工作区的目录监听自动推开关。</param>
/// <param name="Name">工作区显示名（改块名时用）。</param>
/// <param name="WorkflowId">工作区云端工作流 id（填 / 改时用）。</param>
public sealed record RhConfigRequest(string? WorkspaceId, string? AudioFile, string? AudioDir, string? ImageFile, string? Prompt, bool? AutoPush, string? Name, string? WorkflowId);

/// <summary>新增工作区请求体。</summary>
/// <param name="Name">显示名。</param>
/// <param name="WorkflowId">云端工作流 id（可空——先占位后填）。</param>
public sealed record RhWorkspaceAddRequest(string? Name, string? WorkflowId);

/// <summary>删除工作区请求体。</summary>
/// <param name="WorkspaceId">工作区 id。</param>
public sealed record RhWorkspaceRemoveRequest(string? WorkspaceId);

/// <summary>系统对话框请求体。</summary>
/// <param name="Kind">file（选音频）/ image（选图片）/ dir（选目录）。</param>
public sealed record RhPickRequest(string? Kind);

/// <summary>线上识别提交请求体。</summary>
/// <param name="WorkspaceId">工作区 id。</param>
/// <param name="Path">输入完整路径（音频 / 图片）；空则用该工作区配置里的输入。</param>
/// <param name="Prompt">生成任务的提示词；空则用该工作区配置里的提示词。</param>
public sealed record RhSubmitRequest(string? WorkspaceId, string? Path, string? Prompt);

/// <summary>线上识别取消请求体。</summary>
/// <param name="Id">标识（工作区 id + 音频路径）。</param>
public sealed record RhCancelRequest(string? Id);

/// <summary>MOSS 下载请求体——给方案（一次下齐）或给单文件 id。</summary>
/// <param name="Plan">方案 id（int8 / bf16 / base / diffusers / accel）。</param>
/// <param name="FileId">索引 id（单条下载用）。</param>
public sealed record MossDownloadRequest(string? Plan, string? FileId);

/// <summary>MOSS 单文件请求体（取消下载用）。</summary>
/// <param name="FileId">索引 id。</param>
public sealed record MossFileRequest(string? FileId);

/// <summary>MOSS 引擎重启请求体。</summary>
/// <param name="Mode">显存模式：lowvram / normalvram / highvram（空 = 用配置里存的）。</param>
public sealed record MossEngineRequest(string? Mode);

/// <summary>WD Tagger 下载请求体。</summary>
/// <param name="Id">模型 id（kind=model）或文件 id（kind=file）。</param>
/// <param name="Kind">model（整模型两件）/ file（单文件，默认）。</param>
public sealed record TaggerDownloadRequest(string? Id, string? Kind);

/// <summary>WD Tagger 打开目录请求体。</summary>
/// <param name="Path">要打开的目录完整路径。</param>
public sealed record TaggerOpenRequest(string? Path);

/// <summary>实时变声自检请求体（正弦进链，不打麦克风、不落音频）。</summary>
/// <param name="Seconds">正弦秒数（默认 3）。</param>
/// <param name="OutIndex">输出设备索引（-1 = 系统默认）。</param>
/// <param name="SampleRate">设备采样率（默认 48000）。</param>
public sealed record RvcRealtimeSelftestRequest(int Seconds, int OutIndex, int SampleRate);
