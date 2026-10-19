using System;
using System.Threading;
using System.Threading.Tasks;
using ComfyDock.Models;
using ComfyDock.Store;

namespace ComfyDock.Collect;

/// <summary>
/// 采集自检 CLI——不经面板走一遍「账号表 → 套壳浏览器 → 采集」，打印结果后退出。
/// 与面板采集合用同一条代码路径（CollectService 之外的编排只差进度与落盘）。
/// </summary>
public static class CollectCli
{
    /// <summary>执行自检。</summary>
    /// <param name="options">运行选项。</param>
    /// <param name="accountId">账号 id。</param>
    /// <returns>退出码（0 采集成功 / 1 采集失败 / 2 参数或账号问题）。</returns>
    public static async Task<int> RunAsync(AppOptions options, string accountId)
    {
        AccountStore store = new AccountStore(options.SnapshotPath);
        AccountRegistryStore registry = new AccountRegistryStore(options.RegistryPath, options.DataDir);

        // [段1] 首次运行把旧快捷方式目录迁成内置账号表
        int migrated = registry.EnsureMigrated(options.GoogleRoot, store.Load());
        if (migrated > 0)
        {
            Console.WriteLine("账号表已从快捷方式目录迁移 " + migrated + " 条 → " + registry.FilePath);
        }

        AccountEntry? entry = registry.Find(accountId);
        if (entry == null)
        {
            Console.WriteLine("账号不存在：" + accountId);
            Console.WriteLine("账号表：" + registry.FilePath);
            foreach (AccountEntry item in registry.All())
            {
                Console.WriteLine("  " + item.Id + " · " + item.Name + " · " + (item.Enabled ? "启用" : "停用"));
            }

            return 2;
        }

        Console.WriteLine("账号：" + entry.Id + " · " + entry.Name + (entry.Enabled ? "" : "（停用）"));
        Console.WriteLine("数据目录：" + registry.ResolveProfileDir(entry));
        Console.WriteLine("导入源：" + (entry.ImportFrom.Length > 0 ? entry.ImportFrom : "—"));
        Console.WriteLine("套壳：" + options.ShellPath);

        // [段2] 采集——与面板同一条链
        await using ShellClient shell = new ShellClient(options.ShellPath, options.DataDir);
        Collector collector = new Collector(shell, options.Url, options.WorkspaceUrl);
        CollectResult result = await collector.CollectAsync(entry.Id, CancellationToken.None).ConfigureAwait(false);

        Console.WriteLine("状态：" + result.Status);
        Console.WriteLine("昵称：" + result.NickName + " · 手机：" + result.Mobile);
        Console.WriteLine("余额：" + (result.TotalCoin.HasValue ? result.TotalCoin.Value.ToString() : "—"));
        Console.WriteLine("密钥前四位：" + (result.ApiKey.Length > 0 ? result.ApiKey.Substring(0, Math.Min(4, result.ApiKey.Length)) + "…" : "—"));
        Console.WriteLine(
            "算力：获取 " + result.CoinGainTime
            + " · 额度 " + (result.CoinGainAmount.HasValue ? result.CoinGainAmount.Value.ToString() : "—")
            + " · 刷新 " + result.CoinRefreshTime
            + " · 花费 " + (result.CoinSpent.HasValue ? result.CoinSpent.Value.ToString() : "—"));
        Console.WriteLine("工作流：" + result.WorkflowTotal + " 条" + (result.WorkflowError.Length > 0 ? "（" + result.WorkflowError + "）" : ""));

        foreach (WorkflowItem item in result.Workflows)
        {
            Console.WriteLine("  " + item.WorkflowId + " · " + item.Name + " · " + item.LastUpdateTime);
        }

        if (result.Error.Length > 0)
        {
            Console.WriteLine("错误：" + result.Error);
        }

        return string.Equals(result.Status, "ok", StringComparison.Ordinal) ? 0 : 1;
    }
}
