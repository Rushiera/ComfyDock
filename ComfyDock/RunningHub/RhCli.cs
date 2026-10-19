using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyDock.Collect;
using ComfyDock.Store;

namespace ComfyDock.RunningHub;

/// <summary>
/// 线上识别 CLI——不经面板跑一次（试跑 / 排错用）：提交一条、轮询一轮、打印状态。
/// 与面板端点同一内核，只是入口不同。
/// </summary>
public static class RhCli
{
    /// <summary>CLI 输出用的 JSON 选项（驼峰）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>按参数执行 CLI 动作。</summary>
    /// <param name="options">运行选项。</param>
    /// <returns>退出码（0 成功 / 1 动作失败 / 2 环境错误）。</returns>
    public static async Task<int> RunAsync(AppOptions options)
    {
        // [段1] 组内核——账号来源是内置账号表（data/accounts-registry.json）
        var store = new AccountStore(options.SnapshotPath);
        var registry = new AccountRegistryStore(options.RegistryPath, options.DataDir);
        var shell = new ShellClient(options.ShellPath, options.DataDir);
        var collect = new CollectService(options, new Collector(shell, options.Url, options.WorkspaceUrl), store, registry);

        RhService service;
        try
        {
            service = new RhService(
                collect,
                Path.Combine(options.DataDir, "rh-config.json"),
                Path.Combine(options.DataDir, "rh-ledger.json"),
                options.RhOutDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("配置 / 账本读不动：" + ex.Message);
            return 2;
        }

        // [段2] 分派动作——状态 / 提交 / 轮询
        if (options.RhStatus)
        {
            RhStatus status = service.Snapshot();
            Console.WriteLine(JsonSerializer.Serialize(status, JsonOptions));
            return 0;
        }

        if (options.RhSubmit.Length > 0)
        {
            string workspaceId = options.RhWorkspace;
            if (workspaceId.Length == 0)
            {
                RhConfig config = service.CurrentConfig();
                if (config.Workspaces.Count > 0)
                {
                    workspaceId = config.Workspaces[0].Id;
                }
            }

            if (workspaceId.Length == 0)
            {
                Console.Error.WriteLine("没有工作区——配置里至少要有内置那一块");
                return 2;
            }

            RhSubmitResult result = await service.SubmitAsync(workspaceId, options.RhSubmit, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            if (!result.Ok)
            {
                return 1;
            }

            return 0;
        }

        RhPollSummary summary = await service.PollOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(summary, JsonOptions));
        return 0;
    }
}
