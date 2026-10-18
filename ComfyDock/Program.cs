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
using ComfyDock.Moss;
using ComfyDock.RunningHub;
using ComfyDock.Shortcut;
using ComfyDock.Store;
using ComfyDock.Tagger;
using ComfyDock.Tts;

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
        var hub = new BrowserHub(options.ChromePath, Path.Combine(options.DataDir, "browsers.json"));

        // [段2] 回收上一代实例留下的 headless 残留——它们占着用户目录，会让后续采集直接失败
        int swept = await hub.SweepResidualHeadlessAsync(options.GoogleRoot, CancellationToken.None).ConfigureAwait(false);
        if (swept > 0)
        {
            Console.WriteLine("已回收残留 headless 实例 " + swept + " 个");
        }

        var gateway = new CdpGateway(options.ChromePath, hub);
        var service = new CollectService(options, new Collector(gateway, options.Url, options.WorkspaceUrl), store);
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

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:" + options.Port);

        WebApplication app = builder.Build();
        MapEndpoints(app, options, store, service, hub, gateway, local, rh, moss, tts, tagger);

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
    /// <param name="hub">受控浏览器中心。</param>
    /// <param name="gateway">CDP 会话入口。</param>
    /// <param name="local">本地 ComfyUI 入口。</param>
    /// <param name="rh">线上识别服务。</param>
    /// <param name="moss">本地生成（MOSS-Image）服务。</param>
    /// <param name="tts">本地生成（IrodoriTTS）服务。</param>
    /// <param name="tagger">本地生成（WD Tagger 打标）服务。</param>
    private static void MapEndpoints(
        WebApplication app,
        AppOptions options,
        AccountStore store,
        CollectService service,
        BrowserHub hub,
        CdpGateway gateway,
        LocalComfy local,
        RhService rh,
        MossService moss,
        TtsService tts,
        TaggerService tagger)
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

        app.MapPost("/api/open-url", async (OpenUrlRequest request, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.ShortcutPath) || string.IsNullOrWhiteSpace(request.Url))
            {
                return Results.Json(new { ok = false, error = "参数不完整" }, JsonOptions, statusCode: 400);
            }

            ShortcutEntry? target = FindTarget(options, request.ShortcutPath);
            if (target is null || target.ProfileDir.Length == 0)
            {
                return Results.Json(new { ok = false, error = "未解析出该账号的浏览器用户目录" }, JsonOptions, statusCode: 404);
            }

            return await OpenTabResultAsync(hub, target.ProfileDir, request.Url, ct).ConfigureAwait(false);
        });

        app.MapPost("/api/browser/takeover", async (OpenUrlRequest request, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.ShortcutPath))
            {
                return Results.Json(new { ok = false, error = "参数不完整" }, JsonOptions, statusCode: 400);
            }

            ShortcutEntry? target = FindTarget(options, request.ShortcutPath);
            if (target is null || target.ProfileDir.Length == 0)
            {
                return Results.Json(new { ok = false, error = "未解析出该账号的浏览器用户目录" }, JsonOptions, statusCode: 404);
            }

            string url = options.Url;
            if (!string.IsNullOrWhiteSpace(request.Url))
            {
                url = request.Url;
            }

            // [段1] 先结束面板之外的实例，再以受控方式重开
            try
            {
                int killed = await hub.KillByProfileAsync(target.ProfileDir, ct).ConfigureAwait(false);
                BrowserOpenResult opened = await hub.EnsureAsync(target.ProfileDir, url, ct).ConfigureAwait(false);
                return Results.Json(new { ok = true, killed, launched = opened.Launched }, JsonOptions);
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

            ShortcutEntry? target = FindTarget(options, request.ShortcutPath);
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
            TaskRecord item = service.Tasks.Enqueue("delete", label, shortcutPath, () => RunDeleteAsync(gateway, options, service, profileDir, shortcutPath, workflowId));
            return Results.Json(new { queued = true, id = item.Id }, JsonOptions);
        });

        app.MapPost("/api/workflow/upload", (UploadWorkflowRequest request) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.ShortcutPath) || string.IsNullOrWhiteSpace(request.FilePath))
            {
                return Results.Json(new { queued = false, reason = "参数不完整" }, JsonOptions, statusCode: 400);
            }

            ShortcutEntry? target = FindTarget(options, request.ShortcutPath);
            if (target is null)
            {
                return Results.Json(new { queued = false, reason = "快捷方式不在扫描目录内" }, JsonOptions, statusCode: 404);
            }

            string filePath = request.FilePath;
            string profileDir = target.ProfileDir;
            string label = "上传 " + Path.GetFileName(filePath);
            TaskRecord item = service.Tasks.Enqueue("upload", label, target.ShortcutPath, () => RunUploadAsync(gateway, options, profileDir, filePath));
            return Results.Json(new { queued = true, id = item.Id }, JsonOptions);
        });

        app.MapPost("/api/diag/eval", async (EvalRequest request, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.ShortcutPath) || string.IsNullOrWhiteSpace(request.Script))
            {
                return Results.Json(new { ok = false, error = "参数不完整" }, JsonOptions, statusCode: 400);
            }

            ShortcutEntry? target = ShortcutScanner.Scan(options.GoogleRoot)
                .FirstOrDefault(entry => string.Equals(entry.ShortcutPath, request.ShortcutPath, StringComparison.OrdinalIgnoreCase));

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

            ShortcutEntry? target = ShortcutScanner.Scan(options.GoogleRoot)
                .FirstOrDefault(entry => string.Equals(entry.ShortcutPath, request.ShortcutPath, StringComparison.OrdinalIgnoreCase));

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

        app.MapPost("/api/open", async (OpenRequest request, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.ShortcutPath))
            {
                return Results.Json(new { ok = false, error = "shortcutPath 为空" }, JsonOptions, statusCode: 400);
            }

            ShortcutEntry? target = FindTarget(options, request.ShortcutPath);
            if (target is null || target.ProfileDir.Length == 0)
            {
                return Results.Json(new { ok = false, error = "未解析出该账号的浏览器用户目录" }, JsonOptions, statusCode: 404);
            }

            // [段1] 受控打开——保留调试端口，面板此后可控制这个窗口
            return await OpenTabResultAsync(hub, target.ProfileDir, options.Url, ct).ConfigureAwait(false);
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
    }

    /// <summary>按快捷方式路径找扫描结果。</summary>
    /// <param name="options">运行选项。</param>
    /// <param name="shortcutPath">快捷方式完整路径。</param>
    /// <returns>扫描项；找不到返回 null。</returns>
    private static ShortcutEntry? FindTarget(AppOptions options, string shortcutPath)
    {
        return ShortcutScanner.Scan(options.GoogleRoot)
            .FirstOrDefault(entry => string.Equals(entry.ShortcutPath, shortcutPath, StringComparison.OrdinalIgnoreCase));
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
    /// <param name="gateway">CDP 会话入口。</param>
    /// <param name="options">运行选项。</param>
    /// <param name="service">采集服务（快照落点）。</param>
    /// <param name="profileDir">浏览器用户数据目录。</param>
    /// <param name="shortcutPath">快捷方式完整路径。</param>
    /// <param name="workflowId">工作流 id。</param>
    /// <returns>任务结果。</returns>
    private static async Task<TaskOutcome> RunDeleteAsync(CdpGateway gateway, AppOptions options, CollectService service, string profileDir, string shortcutPath, string workflowId)
    {
        var admin = new WorkflowAdmin(gateway, options.WorkspaceUrl);
        WorkflowOpResult outcome = await admin.DeleteAsync(profileDir, workflowId, CancellationToken.None).ConfigureAwait(false);

        // [段1] 站点删成功即从快照摘掉该条——面板立刻反映，不必等下一次采集
        if (outcome.Ok)
        {
            service.RemoveWorkflow(shortcutPath, workflowId);
        }

        return BuildOutcome(outcome, "已删除", "删除失败");
    }

    /// <summary>任务体——上传一个工作流文件。</summary>
    /// <param name="gateway">CDP 会话入口。</param>
    /// <param name="options">运行选项。</param>
    /// <param name="profileDir">浏览器用户数据目录。</param>
    /// <param name="filePath">工作流 JSON 完整路径。</param>
    /// <returns>任务结果。</returns>
    private static async Task<TaskOutcome> RunUploadAsync(CdpGateway gateway, AppOptions options, string profileDir, string filePath)
    {
        var admin = new WorkflowAdmin(gateway, options.WorkspaceUrl);
        WorkflowOpResult outcome = await admin.UploadAsync(profileDir, filePath, 45, CancellationToken.None).ConfigureAwait(false);
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
