using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Moss;

/// <summary>一个索引条目在本机的在位情况。</summary>
public sealed class MossFileState
{
    /// <summary>索引 id。</summary>
    public string Id { get; set; } = "";

    /// <summary>落盘完整路径。</summary>
    public string Path { get; set; } = "";

    /// <summary>是否在位（文件存在且非空）。</summary>
    public bool Present { get; set; }

    /// <summary>实际字节数。</summary>
    public long Bytes { get; set; }

    /// <summary>期望字节数（来自索引）。</summary>
    public long ExpectedBytes { get; set; }
}

/// <summary>
/// 本机 MOSS 运行环境实况——ComfyUI 在哪、内核够不够新、插件在不在、模型缺哪个、显卡多大。
/// 全部来自实测（磁盘 + HTTP + nvidia-smi），不猜。
/// </summary>
public sealed class MossEnvSnapshot
{
    /// <summary>ComfyUI 根目录。</summary>
    public string ComfyRoot { get; set; } = "";

    /// <summary>模型根目录（ComfyUI\models）。</summary>
    public string ModelsRoot { get; set; } = "";

    /// <summary>ComfyUI 是否在跑。</summary>
    public bool Running { get; set; }

    /// <summary>ComfyUI 地址。</summary>
    public string Url { get; set; } = "";

    /// <summary>内核版本（磁盘 comfyui_version.py 或 HTTP /system_stats）。</summary>
    public string KernelVersion { get; set; } = "";

    /// <summary>内核是否带 Flux.2 支持（缺它就跑不了 MOSS 的 128 通道 latent）。</summary>
    public bool Flux2Ok { get; set; }

    /// <summary>插件文件是否在位（custom_nodes\moss-image）。</summary>
    public bool PluginFilesOk { get; set; }

    /// <summary>插件是否已被内核加载（/object_info 里有 MOSSImageUNETLoader）。</summary>
    public bool PluginLoaded { get; set; }

    /// <summary>显卡名。</summary>
    public string GpuName { get; set; } = "";

    /// <summary>显存（MB）；0 = 没测到。</summary>
    public int VramMb { get; set; }

    /// <summary>物理内存（MB）。</summary>
    public long RamMb { get; set; }

    /// <summary>模型盘剩余空间（字节）。</summary>
    public long DiskFreeBytes { get; set; }

    /// <summary>能否出图——内核支持 + 插件在位（在跑时还要求已加载）。</summary>
    public bool CanGenerate { get; set; }

    /// <summary>图像编辑插件文件是否在位（custom_nodes\ComfyUI-Moss-Image-Edit）。</summary>
    public bool EditPluginFilesOk { get; set; }

    /// <summary>图像编辑插件是否已被内核加载（/object_info 里有 MossImageEditLoader）。</summary>
    public bool EditPluginLoaded { get; set; }

    /// <summary>编辑 LoRA 两件是否在位（adapter_model.safetensors + adapter_config.json）。</summary>
    public bool EditAdapterOk { get; set; }

    /// <summary>diffusers 底模是否是可用的编辑快照（目录里有 pipeline_moss_image.py 与 scheduler）。</summary>
    public bool HfSnapshotOk { get; set; }

    /// <summary>编辑链路能否出图——编辑插件 + LoRA + diffusers 快照（在跑时还要求插件已加载）。</summary>
    public bool CanEdit { get; set; }

    /// <summary>编辑链路的前置条件说明（逐条具名，不静默）。</summary>
    public List<string> EditBlockers { get; set; } = new List<string>();

    /// <summary>前置条件说明（逐条具名，不静默）。</summary>
    public List<string> Blockers { get; set; } = new List<string>();

    /// <summary>各索引条目的在位情况。</summary>
    public List<MossFileState> Files { get; set; } = new List<MossFileState>();

    /// <summary>探测时刻。</summary>
    public string CheckedAt { get; set; } = "";
}

/// <summary>
/// 环境探针——只读：磁盘、HTTP、nvidia-smi；不改任何文件。
/// </summary>
public sealed class MossEnv
{
    /// <summary>HTTP 探测超时（毫秒）。</summary>
    private const int ProbeTimeoutMs = 2500;

    /// <summary>复用同一个 HTTP 客户端（避免每次探测新建连接池）。</summary>
    private static readonly HttpClient Client = new HttpClient();

    /// <summary>ComfyUI 根目录。</summary>
    private readonly string _comfyRoot;

    /// <summary>ComfyUI 地址。</summary>
    private readonly string _url;

    /// <summary>以根目录与地址构造。</summary>
    /// <param name="comfyRoot">ComfyUI 根目录（含 main.py 的那一层）。</param>
    /// <param name="url">ComfyUI 地址。</param>
    public MossEnv(string comfyRoot, string url)
    {
        _comfyRoot = comfyRoot;
        _url = url;
    }

    /// <summary>ComfyUI 根目录。</summary>
    public string ComfyRoot => _comfyRoot;

    /// <summary>模型根目录。</summary>
    public string ModelsRoot => Path.Combine(_comfyRoot, "models");

    /// <summary>插件目录根。</summary>
    public string CustomNodesRoot => Path.Combine(_comfyRoot, "custom_nodes");

    /// <summary>MOSS 插件目录。</summary>
    public string PluginDir => Path.Combine(CustomNodesRoot, "moss-image");

    /// <summary>官方完整目录方案（HF Loader）的落点根。</summary>
    public string HfRoot => Path.Combine(ModelsRoot, "MOSS-Image");

    /// <summary>图像编辑插件目录（Chenkin-x/ComfyUI-Moss-Image-Edit）。</summary>
    public string EditPluginDir => Path.Combine(CustomNodesRoot, "ComfyUI-Moss-Image-Edit");

    /// <summary>图像编辑 LoRA 的落点根（models\Moss-Image-Edit）。</summary>
    public string EditAdapterRoot => Path.Combine(ModelsRoot, "Moss-Image-Edit");

    /// <summary>某个索引条目的落盘完整路径。</summary>
    /// <param name="file">索引条目。</param>
    /// <returns>完整路径。</returns>
    public string PathOf(MossFile file)
    {
        string root = file.Target switch
        {
            MossTarget.DiffusionModels => Path.Combine(ModelsRoot, "diffusion_models"),
            MossTarget.TextEncoders => Path.Combine(ModelsRoot, "text_encoders"),
            MossTarget.Vae => Path.Combine(ModelsRoot, "vae"),
            MossTarget.Loras => Path.Combine(ModelsRoot, "loras"),
            MossTarget.MossEditAdapter => EditAdapterRoot,
            _ => HfRoot,
        };

        if (file.SubDir.Length > 0)
        {
            root = Path.Combine(root, file.SubDir);
        }

        return Path.Combine(root, file.ResolvedFileName());
    }

    /// <summary>探测一次环境。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>环境快照。</returns>
    public async Task<MossEnvSnapshot> ProbeAsync(CancellationToken ct)
    {
        var snap = new MossEnvSnapshot
        {
            ComfyRoot = _comfyRoot,
            ModelsRoot = ModelsRoot,
            Url = _url,
            CheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        // [段1] 磁盘侧：内核版本 / Flux.2 支持 / 插件文件
        snap.KernelVersion = ReadKernelVersion();
        snap.Flux2Ok = DetectFlux2();
        snap.PluginFilesOk = File.Exists(Path.Combine(PluginDir, "__init__.py"));
        snap.EditPluginFilesOk = File.Exists(Path.Combine(EditPluginDir, "__init__.py"));
        snap.EditAdapterOk = File.Exists(Path.Combine(EditAdapterRoot, "adapter_model.safetensors"))
            && File.Exists(Path.Combine(EditAdapterRoot, "adapter_config.json"));
        snap.HfSnapshotOk = IsEditSnapshot(Path.Combine(HfRoot, "MOSS-Image-SFT"));

        // [段2] 硬件侧：显卡 / 内存 / 磁盘余量
        (snap.GpuName, snap.VramMb) = ReadGpu();
        snap.RamMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        snap.DiskFreeBytes = FreeBytes(ModelsRoot);

        // [段3] 服务侧：在不在跑、插件有没有被加载
        snap.Running = await ProbeRunningAsync(ct).ConfigureAwait(false);
        if (snap.Running)
        {
            snap.PluginLoaded = await HasNodeAsync("MOSSImageUNETLoader", ct).ConfigureAwait(false);
            snap.EditPluginLoaded = await HasNodeAsync("MossImageEditLoader", ct).ConfigureAwait(false);
            bool latentNode = await HasNodeAsync("EmptyFlux2LatentImage", ct).ConfigureAwait(false);
            if (latentNode)
            {
                snap.Flux2Ok = true;
            }
        }

        // [段4] 依赖在位表
        foreach (MossFile file in MossCatalog.Files)
        {
            string path = PathOf(file);
            long size = 0;
            bool present = false;
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 0)
                {
                    present = true;
                    size = info.Length;
                }
            }
            catch (IOException)
            {
                present = false;
            }

            snap.Files.Add(new MossFileState
            {
                Id = file.Id,
                Path = path,
                Present = present,
                Bytes = size,
                ExpectedBytes = file.Bytes,
            });
        }

        // [段5] 前置条件——逐条具名，不静默
        if (!snap.Flux2Ok)
        {
            snap.Blockers.Add("ComfyUI 内核缺 Flux.2 支持（无 EmptyFlux2LatentImage / comfy.storage）——当前内核 "
                + (snap.KernelVersion.Length > 0 ? snap.KernelVersion : "未知")
                + "，MOSS 节点图跑不起来；需换一份较新的 ComfyUI 内核（见面板说明）");
        }

        if (!snap.PluginFilesOk)
        {
            snap.Blockers.Add("MOSS 插件未安装（custom_nodes\\moss-image 不存在）——点「安装插件」");
        }
        else if (snap.Running && !snap.PluginLoaded)
        {
            snap.Blockers.Add("MOSS 插件文件在位但内核没加载它——重启 ComfyUI 后重新检测（内核太旧时插件会加载失败）");
        }

        snap.CanGenerate = snap.Flux2Ok && snap.PluginFilesOk && (!snap.Running || snap.PluginLoaded);

        // [段6] 图像编辑链路——与文生图分开判定：它只吃 diffusers 快照 + 编辑插件 + 编辑 LoRA
        if (!snap.Flux2Ok)
        {
            snap.EditBlockers.Add("ComfyUI 内核缺 Flux.2 支持——编辑链与文生图共用同一份 VAE 与 latent 口径，内核太旧跑不起来");
        }

        if (!snap.EditPluginFilesOk)
        {
            snap.EditBlockers.Add("图像编辑插件未安装（custom_nodes\\ComfyUI-Moss-Image-Edit 不存在）——点「装编辑插件」");
        }
        else if (snap.Running && !snap.EditPluginLoaded)
        {
            snap.EditBlockers.Add("图像编辑插件文件在位但内核没加载它——重启 ComfyUI 后重新检测"
                + "（插件 import 失败通常是 Python 依赖没装：diffusers 0.38+ / optimum-quanto）");
        }

        if (!snap.HfSnapshotOk)
        {
            snap.EditBlockers.Add("diffusers 底模没下齐（缺 " + Path.Combine(HfRoot, "MOSS-Image-SFT")
                + " 下的 pipeline_moss_image.py 或 scheduler）——编辑链只吃 diffusers 目录，单文件主模型没有参考图通路");
        }

        if (!snap.EditAdapterOk)
        {
            snap.EditBlockers.Add("编辑 LoRA 不在位（缺 " + EditAdapterRoot
                + " 下的 adapter_model.safetensors / adapter_config.json）");
        }

        snap.CanEdit = snap.Flux2Ok && snap.EditPluginFilesOk && snap.HfSnapshotOk && snap.EditAdapterOk
            && (!snap.Running || snap.EditPluginLoaded);
        return snap;
    }

    /// <summary>读内核版本——先磁盘 comfyui_version.py，再 HTTP /system_stats。</summary>
    /// <returns>版本号；读不到返回空串。</returns>
    private string ReadKernelVersion()
    {
        try
        {
            string path = Path.Combine(_comfyRoot, "comfyui_version.py");
            if (File.Exists(path))
            {
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int idx = line.IndexOf("__version__", StringComparison.Ordinal);
                    if (idx < 0)
                    {
                        continue;
                    }

                    int quote = line.IndexOf('"');
                    int last = line.LastIndexOf('"');
                    if (quote >= 0 && last > quote)
                    {
                        return line.Substring(quote + 1, last - quote - 1);
                    }
                }
            }
        }
        catch (IOException)
        {
            return "";
        }

        return "";
    }

    /// <summary>磁盘判定内核是否带 Flux.2 支持（nodes_flux.py 里有 EmptyFlux2LatentImage）。</summary>
    /// <returns>支持返回 true。</returns>
    private bool DetectFlux2()
    {
        try
        {
            string fluxNodes = Path.Combine(_comfyRoot, "comfy_extras", "nodes_flux.py");
            if (!File.Exists(fluxNodes))
            {
                return false;
            }

            // comfy.storage 是插件 import 时就要求的模块——两样都在才算真的够新
            bool storage = File.Exists(Path.Combine(_comfyRoot, "comfy", "storage.py"));
            string text = File.ReadAllText(fluxNodes, Encoding.UTF8);
            return storage && text.Contains("EmptyFlux2LatentImage", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// 磁盘判定某个目录是不是可用的编辑快照——编辑插件要求 snapshot 目录里同时有
    /// <c>pipeline_moss_image.py</c>（它按文件路径 import）与 <c>scheduler\scheduler_config.json</c>（采样器按目录读）。
    /// </summary>
    /// <param name="snapshotDir">候选目录。</param>
    /// <returns>可用返回 true。</returns>
    private static bool IsEditSnapshot(string snapshotDir)
    {
        return File.Exists(Path.Combine(snapshotDir, "pipeline_moss_image.py"))
            && File.Exists(Path.Combine(snapshotDir, "scheduler", "scheduler_config.json"))
            && File.Exists(Path.Combine(snapshotDir, "transformer", "config.json"));
    }

    /// <summary>用 nvidia-smi 读显卡名与显存。</summary>
    /// <returns>(显卡名, 显存 MB)；读不到返回 ("", 0)。</returns>
    private static (string Name, int VramMb) ReadGpu()
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.total --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using Process? process = Process.Start(info);
            if (process is null)
            {
                return ("", 0);
            }

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            string first = output.Trim();
            int newline = first.IndexOf('\n');
            if (newline > 0)
            {
                first = first.Substring(0, newline);
            }

            string[] parts = first.Split(',');
            if (parts.Length < 2)
            {
                return ("", 0);
            }

            string name = parts[0].Trim();
            int mb = 0;
            int.TryParse(parts[1].Trim(), out mb);
            return (name, mb);
        }
        catch (Exception ex) when (ex is IOException || ex is System.ComponentModel.Win32Exception)
        {
            return ("", 0);
        }
    }

    /// <summary>磁盘剩余空间。</summary>
    /// <param name="path">目录路径（取所在卷）。</param>
    /// <returns>字节数；取不到返回 0。</returns>
    private static long FreeBytes(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is null)
            {
                return 0;
            }

            var drive = new DriveInfo(root);
            return drive.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException || ex is ArgumentException)
        {
            return 0;
        }
    }

    /// <summary>HTTP 探测 ComfyUI 是否在跑。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>在跑返回 true。</returns>
    private async Task<bool> ProbeRunningAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeoutMs);
            using HttpResponseMessage response = await Client.GetAsync(_url, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException || ex is UriFormatException)
        {
            return false;
        }
    }

    /// <summary>问内核有没有某个节点类（/object_info/节点名）。</summary>
    /// <param name="nodeName">节点类名。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>有返回 true。</returns>
    private async Task<bool> HasNodeAsync(string nodeName, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeoutMs);
            string url = _url.TrimEnd('/') + "/object_info/" + nodeName;
            using HttpResponseMessage response = await Client.GetAsync(url, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.EnumerateObject().Any();
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException || ex is JsonException || ex is UriFormatException)
        {
            return false;
        }
    }
}
