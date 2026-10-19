using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyDock.Models;
using ComfyDock.Store;

namespace ComfyDock.Collect;

/// <summary>
/// 后台刷新 CLI——把所有启用账号刷一遍并落 <c>data\accounts.json</c>。
/// 走的是套壳浏览器的后台模式（窗口不弹），适合定时 / 脚本里跑；不占端口、不需要面板。
/// </summary>
public static class RefreshCli
{
    /// <summary>执行一次全量刷新。</summary>
    /// <param name="options">运行选项。</param>
    /// <returns>退出码（0 = 全部成功 / 1 = 有失败 / 2 = 无可用账号）。</returns>
    public static async Task<int> RunAsync(AppOptions options)
    {
        AccountStore store = new AccountStore(options.SnapshotPath);
        AccountRegistryStore registry = new AccountRegistryStore(options.RegistryPath, options.DataDir);

        // [段1] 首次运行把旧快捷方式目录迁成内置账号表
        int migrated = registry.EnsureMigrated(options.GoogleRoot, store.Load());
        if (migrated > 0)
        {
            Console.WriteLine("账号表已从快捷方式目录迁移 " + migrated + " 条 → " + registry.FilePath);
        }

        List<AccountEntry> entries = registry.Active();
        if (entries.Count == 0)
        {
            Console.WriteLine("没有启用中的账号：" + registry.FilePath);
            return 2;
        }

        Console.WriteLine("开始刷新 " + entries.Count + " 个账号（后台模式，不弹窗口）");

        // [段2] 逐账号采集——套壳同一时刻只开一个账号视图，串行跑
        AccountSnapshot snapshot = store.Load();
        await using ShellClient shell = new ShellClient(options.ShellPath, options.DataDir);
        Collector collector = new Collector(shell, options.Url, options.WorkspaceUrl);

        int okCount = 0;
        int failCount = 0;
        foreach (AccountEntry entry in entries)
        {
            CollectResult result;
            try
            {
                result = await collector.CollectAsync(entry.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = new CollectResult();
                result.Status = "error";
                result.Error = ex.Message;
            }

            if (string.Equals(result.Status, "ok", StringComparison.Ordinal))
            {
                okCount = okCount + 1;
                Console.WriteLine(
                    "  " + entry.Id + " · " + entry.Name + " · 余额 "
                    + (result.TotalCoin.HasValue ? result.TotalCoin.Value.ToString() : "—")
                    + " · 工作流 " + result.WorkflowTotal + " 条");
            }
            else
            {
                failCount = failCount + 1;
                Console.WriteLine("  " + entry.Id + " · " + entry.Name + " · 失败：" + result.Error);
            }

            Apply(snapshot, registry, entry, result);
            snapshot.GeneratedAt = Clock.Now();
            store.Save(snapshot);
        }

        Console.WriteLine("刷新完成：" + okCount + " 成功 / " + failCount + " 失败 → " + store.FilePath);
        return failCount == 0 ? 0 : 1;
    }

    private static void Apply(AccountSnapshot snapshot, AccountRegistryStore registry, AccountEntry entry, CollectResult outcome)
    {
        AccountSlot slot = new AccountSlot();
        slot.ShortcutName = entry.Name.Length == 0 ? entry.Id : entry.Name;
        slot.ShortcutPath = entry.Id;
        slot.ProfileDir = registry.ResolveProfileDir(entry);
        slot.Status = outcome.Status;
        slot.UserId = outcome.UserId;
        slot.NickName = outcome.NickName;
        slot.Mobile = outcome.Mobile;
        slot.ApiKey = outcome.ApiKey;
        slot.TotalCoin = outcome.TotalCoin;
        slot.CoinGainTime = outcome.CoinGainTime;
        slot.CoinGainAmount = outcome.CoinGainAmount;
        slot.CoinRefreshTime = outcome.CoinRefreshTime;
        slot.CoinSpent = outcome.CoinSpent;
        slot.CollectedAt = Clock.Now();
        slot.Error = outcome.Error;
        slot.Workflows = outcome.Workflows;
        slot.WorkflowError = outcome.WorkflowError;

        int index = snapshot.Accounts.FindIndex(item =>
            string.Equals(item.ShortcutPath, slot.ShortcutPath, StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
        {
            snapshot.Accounts[index] = slot;
        }
        else
        {
            snapshot.Accounts.Add(slot);
        }
    }
}
