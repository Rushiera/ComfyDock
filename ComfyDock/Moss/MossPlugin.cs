using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Moss;

/// <summary>
/// MOSS 插件安装器——把 OpenMOSS/MOSS-Image 仓库里的 <c>comfyui/moss-image</c> 装进 ComfyUI 的
/// <c>custom_nodes</c>，顺带把官方示例工作流放进 ComfyUI 的工作流目录（已存在则不覆盖）。
/// 只解压缩这一层，不动仓库其它内容；不升级内核、不改既有节点。
/// </summary>
public static class MossPlugin
{
    /// <summary>插件包下载地址（GitHub codeload zip——不走 git，避免依赖命令行工具）。</summary>
    private const string ZipUrl = "https://codeload.github.com/OpenMOSS/MOSS-Image/zip/refs/heads/main";

    /// <summary>zip 内插件目录的路径后缀。</summary>
    private const string PluginSuffix = "/comfyui/moss-image/";

    /// <summary>示例工作流在 zip 内的路径后缀。</summary>
    private const string WorkflowSuffix = "/comfyui/moss-image/example_workflows/moss-image.json";

    /// <summary>HTTP 客户端（大文件流式，不设总超时）。</summary>
    private static readonly HttpClient Client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>安装插件。</summary>
    /// <param name="env">环境（决定装到哪）。</param>
    /// <param name="state">状态对象（逐段更新，面板实时显示）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>结果说明。</returns>
    public static async Task<string> InstallAsync(MossEnv env, MossPluginState state, CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(), "moss-plugin-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (HttpResponseMessage response = await Client
                .GetAsync(ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    state.State = "error";
                    state.Message = "插件包下载失败：HTTP " + (int)response.StatusCode;
                    return state.Message;
                }

                using Stream input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var output = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }

            state.State = "extracting";
            state.Message = "正在解包到 custom_nodes\\moss-image …";

            int files = ExtractPlugin(tempZip, env.PluginDir);
            if (files == 0)
            {
                state.State = "error";
                state.Message = "包里没找到 comfyui/moss-image——仓库结构可能变了，去 GitHub 核对";
                return state.Message;
            }

            string patchNote = PatchLoaders(env.PluginDir);
            string workflowNote = SeedWorkflow(tempZip, env.ComfyRoot);

            state.State = "done";
            state.Message = "插件已装（" + files + " 个文件）——重启 ComfyUI 后生效" + patchNote + workflowNote;
            return state.Message;
        }
        finally
        {
            try
            {
                if (File.Exists(tempZip))
                {
                    File.Delete(tempZip);
                }
            }
            catch (IOException ex)
            {
                // 临时文件删不掉不影响结果
                Console.Error.WriteLine("清理插件包失败：" + ex.Message);
            }
        }
    }

    /// <summary>从 zip 里只解 comfyui/moss-image 这一层。</summary>
    /// <param name="zipPath">zip 路径。</param>
    /// <param name="destDir">目标目录。</param>
    /// <returns>解出的文件数。</returns>
    private static int ExtractPlugin(string zipPath, string destDir)
    {
        int count = 0;
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        Directory.CreateDirectory(destDir);

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            int idx = name.IndexOf(PluginSuffix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                continue;
            }

            string rel = name.Substring(idx + PluginSuffix.Length);
            if (rel.Length == 0 || rel.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            string target = Path.Combine(destDir, rel.Replace('/', Path.DirectorySeparatorChar));
            string? parent = Path.GetDirectoryName(target);
            if (parent is not null)
            {
                Directory.CreateDirectory(parent);
            }

            entry.ExtractToFile(target, true);
            count++;
        }

        return count;
    }

    /// <summary>把官方示例工作流放进 ComfyUI 的工作流目录（同名已存在则跳过）。</summary>
    /// <param name="zipPath">zip 路径。</param>
    /// <param name="comfyRoot">ComfyUI 根目录。</param>
    /// <returns>结果说明（已放 / 已存在 / 没找到）。</returns>
    private static string SeedWorkflow(string zipPath, string comfyRoot)
    {
        string dir = Path.Combine(comfyRoot, "user", "default", "workflows");
        string target = Path.Combine(dir, "moss-image.json");
        if (File.Exists(target))
        {
            return "；示例工作流已存在，未覆盖";
        }

        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (!name.EndsWith(WorkflowSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Directory.CreateDirectory(dir);
            entry.ExtractToFile(target, true);
            return "；示例工作流已放进 ComfyUI 的 workflows 目录";
        }

        return "；包里没找到示例工作流";
    }
    /// <summary>
    /// 给插件的 `loaders.py` 打「合并 qkv 拆分」补丁——Comfy-Org 的单文件权重是**合并 qkv** 口径
    /// （`layers.N.proj.qkv.weight`），而插件模块是 `proj.q / k / v` 分开的；不拆则权重赋不上，
    /// 推理时 `self.proj["q"].weight` 为 None（`'NoneType' object has no attribute 'dtype'`，2026-10-18 实测）。
    /// 幂等：已经打过就跳过。
    /// </summary>
    /// <param name="pluginDir">插件目录。</param>
    /// <returns>说明（空 = 没动）。</returns>
    private static string PatchLoaders(string pluginDir)
    {
        string path = Path.Combine(pluginDir, "loaders.py");
        if (!File.Exists(path))
        {
            return "";
        }

        string text = File.ReadAllText(path);
        if (text.IndexOf("def split_qkv(", StringComparison.Ordinal) >= 0)
        {
            return "（qkv 拆分补丁已在位）";
        }

        const string Function = """
    def split_qkv(state):
        # ComfyDock 补丁（2026-10-18）：Comfy-Org 单文件权重是合并 qkv 口径，插件模块是 proj.q/k/v 分开的；
        # 不拆则权重赋不上，推理时 self.proj["q"].weight 为 None。
        targets = [key for key in state.keys() if ".proj.qkv." in key]
        if not targets:
            return state
        out = dict(state)
        for key in targets:
            head, _, tail = key.rpartition(".proj.qkv.")
            if tail == "comfy_quant":
                blob = out.pop(key)
                for name in ("q", "k", "v"):
                    out[f"{head}.proj.{name}.comfy_quant"] = blob
                continue
            if tail not in ("weight", "bias", "weight_scale"):
                continue
            tensor = out.pop(key)
            if tensor.shape[0] % 3 != 0:
                raise ValueError(f"qkv 维度不是 3 的倍数：{key} {tuple(tensor.shape)}")
            chunk = tensor.shape[0] // 3
            for index, name in enumerate(("q", "k", "v")):
                out[f"{head}.proj.{name}.{tail}"] = tensor[index * chunk:(index + 1) * chunk]
        return out


    """;

        const string Anchor = "    config = model_config(state)";
        if (text.IndexOf(Anchor, StringComparison.Ordinal) < 0)
        {
            return "（插件结构变了，qkv 补丁未打——去 GitHub 核对 loaders.py）";
        }

        text = text.Replace(Anchor, "    state = split_qkv(state)\n" + Anchor);
        text = text.Replace("def load_model_state(", Function + "def load_model_state(");
        File.WriteAllText(path, text);
        return "（已打 qkv 拆分补丁）";
    }
}
