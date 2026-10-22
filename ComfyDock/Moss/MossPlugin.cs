using System;
using System.Diagnostics;
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

    /// <summary>图像编辑插件包下载地址（Chenkin-x/ComfyUI-Moss-Image-Edit 的 codeload zip）。</summary>
    private const string EditZipUrl = "https://codeload.github.com/Chenkin-x/ComfyUI-Moss-Image-Edit/zip/refs/heads/main";

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

    /// <summary>
    /// 给编辑插件的 nodes.py 打「8 步 DMD 并入底模」补丁——编辑插件没有第二个 LoRA 输入口，
    /// 只能把 DMD 按 lora_down/lora_up 口径并进 Linear 权重，再让编辑 LoRA 照常叠在上面。
    /// 幂等：已打过就跳过；插件升级覆盖后重装会重新打上。
    /// </summary>
    /// <param name="pluginDir">编辑插件目录。</param>
    /// <returns>说明（空 = 没动）。</returns>
    private static string PatchEditExtraLora(string pluginDir)
    {
        string path = Path.Combine(pluginDir, "nodes.py");
        if (!File.Exists(path))
        {
            return "";
        }

        string text = File.ReadAllText(path);
        if (text.IndexOf("def merge_extra_lora(", StringComparison.Ordinal) >= 0)
        {
            return "（DMD 并入补丁已在位）";
        }

        if (text.IndexOf("def _model_path(", StringComparison.Ordinal) < 0
            || text.IndexOf("model = load_transformer(self.snapshot", StringComparison.Ordinal) < 0)
        {
            return "（插件结构变了，DMD 补丁未打——去 GitHub 核对 nodes.py）";
        }

        if (text.IndexOf("import os", StringComparison.Ordinal) < 0)
        {
            text = text.Replace("import math\n", "import math\nimport os\n");
            if (text.IndexOf("import os", StringComparison.Ordinal) < 0)
            {
                return "（插件结构变了，DMD 补丁未打——找不到 import 段）";
            }
        }

        const string Function =
            "def merge_extra_lora(model):\n"
            + "    # ComfyDock 补丁（2026-10-22）：把 8 步 DMD LoRA 合并进底模权重。\n"
            + "    # 编辑插件只认 adapter_dir 里那份编辑 LoRA，节点没有第二个 LoRA 输入口；\n"
            + "    # 这里按 ComfyUI 的 lora_down/lora_up 口径直接并进 Linear 权重（编辑 LoRA 照常叠在上面）。\n"
            + "    # 开关：MOSS_EDIT_EXTRA_LORA 给路径（空 = 用 models/loras/MOSS 下的 DMD），MOSS_EDIT_EXTRA_STRENGTH 给强度。\n"
            + "    path = os.environ.get('MOSS_EDIT_EXTRA_LORA', '')\n"
            + "    if not path:\n"
            + "        path = str(Path(folder_paths.models_dir) / 'loras' / 'MOSS' / 'MOSS-SFT-8step-DMD.safetensors')\n"
            + "    if not Path(path).is_file():\n"
            + "        logging.info('MOSS Image Edit: extra LoRA not found, skipped: %s', path)\n"
            + "        return\n"
            + "\n"
            + "    strength = float(os.environ.get('MOSS_EDIT_EXTRA_STRENGTH', '1.0'))\n"
            + "    from safetensors import safe_open\n"
            + "\n"
            + "    with safe_open(path, framework='pt', device='cpu') as handle:\n"
            + "        meta = handle.metadata() or {}\n"
            + "        rank = float(meta.get('rank', 128))\n"
            + "        merged = 0\n"
            + "        for key in handle.keys():\n"
            + "            if not key.endswith('.lora_down.weight'):\n"
            + "                continue\n"
            + "            name = key[len('diffusion_model.'):-len('.lora_down.weight')]\n"
            + "            up = handle.get_tensor('diffusion_model.' + name + '.lora_up.weight').float()\n"
            + "            down = handle.get_tensor(key).float()\n"
            + "            alpha = rank\n"
            + "            alpha_key = 'diffusion_model.' + name + '.alpha'\n"
            + "            if alpha_key in handle.keys():\n"
            + "                alpha = float(handle.get_tensor(alpha_key))\n"
            + "            module = model.get_submodule(name)\n"
            + "            delta = (up @ down) * (alpha / rank) * strength\n"
            + "            module.weight.data += delta.to(module.weight.dtype)\n"
            + "            merged += 1\n"
            + "    logging.info('MOSS Image Edit: merged extra LoRA %d layers (strength %.2f)', merged, strength)\n"
            + "\n"
            + "\n";

        text = text.Replace("def _model_path(", Function + "def _model_path(");
        text = text.Replace(
            "            model = load_transformer(self.snapshot, device=\"cpu\", dtype=torch.bfloat16)\n",
            "            model = load_transformer(self.snapshot, device=\"cpu\", dtype=torch.bfloat16)\n"
            + "            merge_extra_lora(model)\n");
        File.WriteAllText(path, text);
        return "（已打 DMD 并入补丁）";
    }

    /// <summary>
    /// 安装图像编辑插件（Chenkin-x/ComfyUI-Moss-Image-Edit）——整包解到 <c>custom_nodes\ComfyUI-Moss-Image-Edit</c>，
    /// 只跳过 <c>.github</c> 与 <c>assets</c>（示例图，与推理无关）。
    /// 依赖不在这里装：插件 requirements 钉 <c>transformers==5.5.3</c>，直接装会把本机 5.20.1 降级、连带砸掉音频链，
    /// 所以拆成两步，依赖走 <see cref="InstallEditDepsAsync"/>。
    /// </summary>
    /// <param name="env">环境（决定装到哪）。</param>
    /// <param name="state">状态对象（逐段更新，面板实时显示）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>结果说明。</returns>
    public static async Task<string> InstallEditAsync(MossEnv env, MossPluginState state, CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(), "moss-edit-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            state.State = "downloading";
            state.Message = "正在下载图像编辑插件包…";

            using (HttpResponseMessage response = await Client
                .GetAsync(EditZipUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    state.State = "error";
                    state.Message = "编辑插件包下载失败：HTTP " + (int)response.StatusCode;
                    return state.Message;
                }

                using Stream input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var output = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }

            state.State = "extracting";
            state.Message = "正在解包到 custom_nodes 的 ComfyUI-Moss-Image-Edit …";

            int files = ExtractEditPlugin(tempZip, env.EditPluginDir);
            if (files == 0)
            {
                state.State = "error";
                state.Message = "包里没找到插件文件——仓库结构可能变了，去 GitHub 核对";
                return state.Message;
            }

            string patchNote = PatchEditExtraLora(env.EditPluginDir);

            state.State = "done";
            state.Message = "编辑插件已装（" + files + " 个文件）" + patchNote + "——接着点「装编辑依赖」，再重启 ComfyUI";
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
                Console.Error.WriteLine("清理编辑插件包失败：" + ex.Message);
            }
        }
    }

    /// <summary>
    /// 装编辑插件的 Python 依赖——<c>diffusers&gt;=0.38</c> · <c>accelerate&gt;=1.13</c> · <c>optimum-quanto==0.2.7</c>。
    /// <b>不装 transformers</b>：插件 requirements 钉 5.5.3，而本机（TTS / VITS 依赖）是 5.20.1，降级会连带砸掉音频链；
    /// 官方 pipeline 自己声明的是 <c>transformers&gt;=5.3</c>，先用现有的试。
    /// </summary>
    /// <param name="env">环境（取 ComfyUI 的解释器）。</param>
    /// <param name="state">状态对象（逐段更新）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>结果说明。</returns>
    public static async Task<string> InstallEditDepsAsync(MossEnv env, MossPluginState state, CancellationToken ct)
    {
        string python = ResolvePython(env.ComfyRoot);
        if (python.Length == 0)
        {
            state.State = "error";
            state.Message = "找不到 ComfyUI 的 python_embeded\\python.exe——检查 ComfyUI 根目录";
            return state.Message;
        }

        state.State = "deps";
        state.Message = "正在装依赖（diffusers / accelerate / optimum-quanto，不碰 transformers）…";

        var info = new ProcessStartInfo
        {
            FileName = python,
            Arguments = "-m pip install --upgrade diffusers>=0.38 accelerate>=1.13.0 optimum-quanto==0.2.7 "
                + "-i https://pypi.tuna.tsinghua.edu.cn/simple",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using Process? process = Process.Start(info);
            if (process is null)
            {
                state.State = "error";
                state.Message = "pip 没起来（Process.Start 返回空）";
                return state.Message;
            }

            Task<string> outTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            string output = await outTask.ConfigureAwait(false);
            string error = await errTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                state.State = "error";
                string detail = error.Length > 0 ? error : output;
                state.Message = "依赖安装失败（exit " + process.ExitCode + "）：" + Tail(detail, 600);
                return state.Message;
            }

            state.State = "done";
            state.Message = "依赖已装——重启 ComfyUI 让插件加载：" + Tail(output, 300);
            return state.Message;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is OperationCanceledException)
        {
            state.State = "error";
            state.Message = "依赖安装出错：" + ex.Message;
            return state.Message;
        }
    }

    /// <summary>从 zip 里解出编辑插件整包（跳过 .github 与 assets）。</summary>
    /// <param name="zipPath">zip 路径。</param>
    /// <param name="destDir">目标目录。</param>
    /// <returns>解出的文件数。</returns>
    private static int ExtractEditPlugin(string zipPath, string destDir)
    {
        int count = 0;
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        Directory.CreateDirectory(destDir);

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            int slash = name.IndexOf('/');
            if (slash < 0)
            {
                continue;
            }

            string rel = name.Substring(slash + 1);
            if (rel.Length == 0 || rel.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            if (rel.StartsWith(".github/", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>找 ComfyUI 的 python_embeded\python.exe（便携版在 ComfyUI 的上一级）。</summary>
    /// <param name="comfyRoot">ComfyUI 根目录。</param>
    /// <returns>解释器完整路径；找不到返回空串。</returns>
    private static string ResolvePython(string comfyRoot)
    {
        string? parent = Path.GetDirectoryName(comfyRoot);
        if (parent is not null)
        {
            string candidate = Path.Combine(parent, "python_embeded", "python.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string inline = Path.Combine(comfyRoot, "python_embeded", "python.exe");
        if (File.Exists(inline))
        {
            return inline;
        }

        return "";
    }

    /// <summary>取输出尾部（回执截断用——pip 日志很长，只留结论段）。</summary>
    /// <param name="text">原始输出。</param>
    /// <param name="limit">保留字符数。</param>
    /// <returns>压成单行的尾部。</returns>
    private static string Tail(string text, int limit)
    {
        string flat = text.Replace("\r", " ").Replace("\n", " ");
        if (flat.Length <= limit)
        {
            return flat;
        }

        return "…" + flat.Substring(flat.Length - limit);
    }
}
