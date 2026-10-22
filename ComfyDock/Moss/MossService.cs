using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Moss;

/// <summary>MOSS 面板的持久化配置（生成参数 + 选中的方案 / 显存模式）。</summary>
public sealed class MossConfig
{
    /// <summary>选中的下载方案 id（int8 / bf16 / base / diffusers）。</summary>
    public string Plan { get; set; } = "int8";

    /// <summary>显存使用方式（lowvram / normalvram / highvram；空 = 沿用引擎当前启动参数）。</summary>
    public string VramMode { get; set; } = "";

    /// <summary>正向提示词。</summary>
    public string Prompt { get; set; } = "";

    /// <summary>负向提示词。</summary>
    public string Negative { get; set; } = "";

    /// <summary>宽（自动对齐 16）。</summary>
    public int Width { get; set; } = 1024;

    /// <summary>高（自动对齐 16）。</summary>
    public int Height { get; set; } = 1024;

    /// <summary>步数。</summary>
    public int Steps { get; set; } = 50;

    /// <summary>CFG。</summary>
    public double Cfg { get; set; } = 3.0;

    /// <summary>Shift。</summary>
    public double Shift { get; set; } = 5.0;

    /// <summary>种子（-1 = 每次随机）。</summary>
    public long Seed { get; set; } = -1;

    /// <summary>批次张数。</summary>
    public int Batch { get; set; } = 1;

    /// <summary>8 步加速开关。</summary>
    public bool Accel { get; set; }

    /// <summary>加速 LoRA 文件名。</summary>
    public string AccelLora { get; set; } = "MOSS/MOSS-SFT-8step-DMD.safetensors";

    /// <summary>加速 LoRA 权重。</summary>
    public double AccelStrength { get; set; } = 1.0;

    /// <summary>结构增强（SLG）开关。</summary>
    public bool Slg { get; set; } = true;

    /// <summary>SLG 跳过层。</summary>
    public string SlgLayers { get; set; } = "22";

    /// <summary>用户 LoRA 文件名（空 = 不用）。</summary>
    public string LoraName { get; set; } = "";

    /// <summary>用户 LoRA 权重。</summary>
    public double LoraStrength { get; set; } = 1.0;

    /// <summary>Hires 第二遍开关。</summary>
    public bool Hires { get; set; }

    /// <summary>Hires 放大倍率。</summary>
    public double HiresScale { get; set; } = 1.5;

    /// <summary>Hires 重绘幅度。</summary>
    public double HiresDenoise { get; set; } = 0.5;

    /// <summary>图生图输入图片完整路径（空 = 文生图）。</summary>
    public string InputImage { get; set; } = "";

    /// <summary>图生图重绘幅度。</summary>
    public double ImgDenoise { get; set; } = 0.6;

    /// <summary>参考图完整路径（1..4 张，顺序即 image 1..4）——有图即图像编辑，空着即文生图。</summary>
    public List<string> RefImages { get; set; } = new List<string>();

    /// <summary>参考图长边上限（缩到它、16 对齐、不裁剪；官方 512）。</summary>
    public int RefMaxEdge { get; set; } = 512;

}

/// <summary>插件安装状态。</summary>
public sealed class MossPluginState
{
    /// <summary>状态：idle / downloading / extracting / done / error。</summary>
    public string State { get; set; } = "idle";

    /// <summary>说明 / 失败原因。</summary>
    public string Message { get; set; } = "";

    /// <summary>插件目录。</summary>
    public string Dir { get; set; } = "";
}

/// <summary>
/// 本地生成板块（MOSS-Image）的服务面——环境探针 + 依赖索引/下载 + 插件安装 + 生成投递与轮询。
/// 与线上识别平行：这里全部走本机 ComfyUI 的 HTTP API。
/// </summary>
public sealed class MossService
{
    /// <summary>配置落盘路径。</summary>
    private readonly string _configPath;

    /// <summary>产物落点目录。</summary>
    private readonly string _outDir;

    /// <summary>状态锁。</summary>
    private readonly object _gate = new object();

    /// <summary>环境探针。</summary>
    private readonly MossEnv _env;

    /// <summary>节点图投递器。</summary>
    private readonly MossRunner _runner;

    /// <summary>下载器。</summary>
    private readonly MossDownloader _downloader = new MossDownloader();

    /// <summary>当前配置。</summary>
    private MossConfig _config;

    /// <summary>当前任务状态。</summary>
    private MossJobStatus _job = new MossJobStatus();

    /// <summary>插件安装状态。</summary>
    private MossPluginState _plugin = new MossPluginState();

    /// <summary>图像编辑插件的安装状态（与主插件分开——两条安装链各自独立）。</summary>
    private MossPluginState _editPlugin = new MossPluginState();

    /// <summary>轮询是否已被要求停止。</summary>
    private bool _stopPoll;
    /// <summary>内置推理引擎——ComfyUI 没在跑时走这条，用它的解释器与模型，但不启它的服务。</summary>
    private readonly MossLocalEngine _local;

    /// <summary>ComfyUI 的 input 目录（图生图入口）。</summary>
    private readonly string _inputDir;

    /// <summary>ComfyUI 根目录（按显存模式重启引擎时用）。</summary>
    private readonly string _comfyRoot;

    /// <summary>ComfyUI 地址（同上）。</summary>
    private readonly string _comfyUrl;

    /// <summary>以选项构造。</summary>
    /// <param name="comfyRoot">ComfyUI 根目录。</param>
    /// <param name="comfyUrl">ComfyUI 地址。</param>
    /// <param name="dataDir">数据目录（配置落点）。</param>
    /// <param name="outDir">产物落点目录。</param>
    public MossService(string comfyRoot, string comfyUrl, string dataDir, string outDir)
    {
        _env = new MossEnv(comfyRoot, comfyUrl);
        _runner = new MossRunner(comfyUrl);
        _local = new MossLocalEngine();
        _local.Prepare(comfyRoot);
        _configPath = Path.Combine(dataDir, "moss-config.json");
        _outDir = outDir;
        _inputDir = Path.Combine(comfyRoot, "input");
        _comfyRoot = comfyRoot;
        _comfyUrl = comfyUrl;
        _config = LoadConfig();
        _downloader.Start();
        _plugin.Dir = _env.PluginDir;
        _editPlugin.Dir = _env.EditPluginDir;
    }

    /// <summary>产物落点目录。</summary>
    public string OutDir => _outDir;

    /// <summary>插件安装状态。</summary>
    public MossPluginState Plugin => _plugin;

    /// <summary>图像编辑插件的安装状态。</summary>
    public MossPluginState EditPlugin => _editPlugin;

    /// <summary>当前任务状态（拷贝，防外部改）。</summary>
    /// <returns>任务状态副本。</returns>
    public MossJobStatus Job()
    {
        lock (_gate)
        {
            return new MossJobStatus
            {
                Id = _job.Id,
                State = _job.State,
                PromptId = _job.PromptId,
                Message = _job.Message,
                Images = new List<string>(_job.Images),
                Seed = _job.Seed,
                Steps = _job.Steps,
                SubmittedAt = _job.SubmittedAt,
                FinishedAt = _job.FinishedAt,
            };
        }
    }

    /// <summary>下载任务清单（面板轮询用）。</summary>
    /// <returns>任务列表（新→旧）。</returns>
    public List<MossDownloadJob> Downloads()
    {
        return _downloader.Snapshot();
    }

    /// <summary>面板快照——环境 + 方案 + 配置 + 下载 + 任务。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>快照对象（匿名结构，直接序列化给前端）。</returns>
    public async Task<object> SnapshotAsync(CancellationToken ct)
    {
        MossEnvSnapshot env = await _env.ProbeAsync(ct).ConfigureAwait(false);
        List<MossDownloadJob> jobs = _downloader.Snapshot();

        var present = new Dictionary<string, MossFileState>(StringComparer.OrdinalIgnoreCase);
        foreach (MossFileState state in env.Files)
        {
            present[state.Id] = state;
        }

        var plans = new List<object>();
        foreach (MossPlan plan in MossCatalog.Plans)
        {
            var files = new List<object>();
            bool ready = true;
            foreach (string id in plan.Files)
            {
                MossFile? file = MossCatalog.Find(id);
                if (file is null)
                {
                    continue;
                }

                MossFileState? state = present.TryGetValue(id, out MossFileState? s) ? s : null;
                bool ok = state is not null && state.Present;
                if (!ok)
                {
                    ready = false;
                }

                files.Add(new
                {
                    id = file.Id,
                    title = file.Title,
                    note = file.Note,
                    bytes = file.Bytes,
                    present = ok,
                    path = state?.Path ?? _env.PathOf(file),
                    target = file.Target.ToString(),
                });
            }

            plans.Add(new
            {
                id = plan.Id,
                title = plan.Title,
                note = plan.Note,
                minVramMb = plan.MinVramMb,
                recommended = plan.Recommended,
                bytes = MossCatalog.PlanBytes(plan),
                ready,
                files,
            });
        }

        return new
        {
            env = new
            {
                comfyRoot = env.ComfyRoot,
                modelsRoot = env.ModelsRoot,
                running = env.Running,
                url = env.Url,
                kernelVersion = env.KernelVersion,
                flux2Ok = env.Flux2Ok,
                pluginFilesOk = env.PluginFilesOk,
                pluginLoaded = env.PluginLoaded,
                gpuName = env.GpuName,
                vramMb = env.VramMb,
                ramMb = env.RamMb,
                diskFreeBytes = env.DiskFreeBytes,
                canGenerate = env.CanGenerate,
                blockers = env.Blockers,
                editPluginFilesOk = env.EditPluginFilesOk,
                editPluginLoaded = env.EditPluginLoaded,
                editAdapterOk = env.EditAdapterOk,
                hfSnapshotOk = env.HfSnapshotOk,
                canEdit = env.CanEdit,
                editBlockers = env.EditBlockers,
                checkedAt = env.CheckedAt,
            },
            recommendPlan = MossCatalog.RecommendPlan(env.VramMb),
            recommendVramMode = MossCatalog.RecommendVramMode(env.VramMb),
            config = _config,
            plans,
            downloads = jobs,
            job = Job(),
            plugin = _plugin,
            editPlugin = _editPlugin,
            local = _local.State,
            outDir = _outDir,
            loras = ListModels("loras", "*.safetensors"),
            unets = ListModels("diffusion_models", "*.safetensors"),
            clips = ListModels("text_encoders", "*.safetensors"),
            vaes = ListModels("vae", "*.safetensors"),
        };
    }

    /// <summary>改配置（传进来的非空字段覆盖，其余不动）。</summary>
    /// <param name="next">新配置。</param>
    /// <returns>落盘后的配置。</returns>
    public MossConfig UpdateConfig(MossConfig next)
    {
        lock (_gate)
        {
            if (next.Plan.Length > 0)
            {
                _config.Plan = next.Plan;
            }

            _config.VramMode = next.VramMode;
            _config.Prompt = next.Prompt;
            _config.Negative = next.Negative;
            _config.Width = Clamp(next.Width, 256, 2048);
            _config.Height = Clamp(next.Height, 256, 2048);
            _config.Steps = Clamp(next.Steps, 1, 100);
            _config.Cfg = Clamp(next.Cfg, 0.0, 20.0);
            _config.Shift = Clamp(next.Shift, 0.0, 20.0);
            _config.Seed = next.Seed;
            _config.Batch = Clamp(next.Batch, 1, 8);
            _config.Accel = next.Accel;
            if (next.AccelLora.Length > 0)
            {
                _config.AccelLora = next.AccelLora;
            }

            _config.AccelStrength = Clamp(next.AccelStrength, 0.0, 2.0);
            _config.Slg = next.Slg;
            if (next.SlgLayers.Length > 0)
            {
                _config.SlgLayers = next.SlgLayers;
            }

            _config.LoraName = next.LoraName;
            _config.LoraStrength = Clamp(next.LoraStrength, 0.0, 2.0);
            _config.Hires = next.Hires;
            _config.HiresScale = Clamp(next.HiresScale, 1.0, 3.0);
            _config.HiresDenoise = Clamp(next.HiresDenoise, 0.05, 1.0);
            _config.InputImage = next.InputImage;
            _config.ImgDenoise = Clamp(next.ImgDenoise, 0.05, 1.0);
            _config.RefImages = new List<string>(next.RefImages);
            _config.RefMaxEdge = Clamp(next.RefMaxEdge, 16, 4096);
            SaveConfig();
            return _config;
        }
    }

    /// <summary>入队一个方案的缺失文件。</summary>
    /// <param name="planId">方案 id。</param>
    /// <returns>(入队条数, 逐条说明)。</returns>
    public (int Queued, List<string> Notes) DownloadPlan(string planId)
    {
        var notes = new List<string>();
        MossPlan? plan = MossCatalog.FindPlan(planId);
        if (plan is null)
        {
            notes.Add("没有这个方案：" + planId);
            return (0, notes);
        }

        int queued = 0;
        foreach (string id in plan.Files)
        {
            MossFile? file = MossCatalog.Find(id);
            if (file is null)
            {
                continue;
            }

            string target = _env.PathOf(file);
            if (File.Exists(target) && (file.Bytes <= 0 || new FileInfo(target).Length == file.Bytes))
            {
                notes.Add("已在位：" + file.Title);
                continue;
            }

            if (_downloader.Enqueue(file, target, out string reason))
            {
                queued++;
                notes.Add("已入队：" + file.Title);
            }
            else
            {
                notes.Add("跳过：" + file.Title + "（" + reason + "）");
            }
        }

        return (queued, notes);
    }

    /// <summary>入队单个文件。</summary>
    /// <param name="fileId">索引 id。</param>
    /// <returns>(是否入队, 说明)。</returns>
    public (bool Ok, string Note) DownloadFile(string fileId)
    {
        MossFile? file = MossCatalog.Find(fileId);
        if (file is null)
        {
            return (false, "索引里没有这个 id：" + fileId);
        }

        string target = _env.PathOf(file);
        bool ok = _downloader.Enqueue(file, target, out string reason);
        return (ok, ok ? "已入队：" + file.Title : reason);
    }

    /// <summary>取消一条下载。</summary>
    /// <param name="fileId">索引 id。</param>
    /// <returns>取消成功返回 true。</returns>
    public bool CancelDownload(string fileId)
    {
        return _downloader.Cancel(fileId);
    }

    /// <summary>装图像编辑插件（Chenkin-x/ComfyUI-Moss-Image-Edit）——整包解到 custom_nodes。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>说明。</returns>
    public async Task<string> InstallEditPluginAsync(CancellationToken ct)
    {
        _editPlugin.State = "downloading";
        _editPlugin.Message = "开始装图像编辑插件…";
        try
        {
            return await MossPlugin.InstallEditAsync(_env, _editPlugin, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is IOException
            || ex is InvalidOperationException || ex is OperationCanceledException)
        {
            _editPlugin.State = "error";
            _editPlugin.Message = "装编辑插件失败：" + ex.Message;
            return _editPlugin.Message;
        }
    }

    /// <summary>装图像编辑插件的 Python 依赖（不碰 transformers——理由见 MossPlugin 的说明）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>说明。</returns>
    public async Task<string> InstallEditDepsAsync(CancellationToken ct)
    {
        _editPlugin.State = "deps";
        _editPlugin.Message = "开始装编辑依赖…";
        try
        {
            return await MossPlugin.InstallEditDepsAsync(_env, _editPlugin, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is OperationCanceledException)
        {
            _editPlugin.State = "error";
            _editPlugin.Message = "装编辑依赖失败：" + ex.Message;
            return _editPlugin.Message;
        }
    }

    /// <summary>装 MOSS 插件（下载仓库 zip → 只解 comfyui/moss-image → 顺带放一份示例工作流）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>说明。</returns>
    public async Task<string> InstallPluginAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_plugin.State == "downloading" || _plugin.State == "extracting")
            {
                return "正在安装中，别重复点";
            }

            _plugin.State = "downloading";
            _plugin.Message = "正在下载插件包（OpenMOSS/MOSS-Image · comfyui/moss-image）…";
        }

        try
        {
            string message = await MossPlugin.InstallAsync(_env, _plugin, ct).ConfigureAwait(false);
            return message;
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is InvalidOperationException)
        {
            lock (_gate)
            {
                _plugin.State = "error";
                _plugin.Message = "安装失败：" + ex.Message;
            }

            return _plugin.Message;
        }
    }

    /// <summary>按当前配置投递一次生成。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 说明)。</returns>
    public async Task<(bool Ok, string Message)> GenerateAsync(CancellationToken ct)
    {
        MossEnvSnapshot env = await _env.ProbeAsync(ct).ConfigureAwait(false);
        if (IsEditMode())
        {
            if (!env.CanEdit)
            {
                string editReason = env.EditBlockers.Count > 0 ? env.EditBlockers[0] : "编辑链路前置条件不足";
                return (false, editReason);
            }
        }
        else if (!env.CanGenerate)
        {
            string reason = env.Blockers.Count > 0 ? env.Blockers[0] : "前置条件不足";
            return (false, reason);
        }

        MossGenParams p;
        lock (_gate)
        {
            if (_job.State == "submitted" || _job.State == "running")
            {
                return (false, "上一张还在跑——先等它出来，或点「中断」");
            }

            p = BuildParams(env, out string error);
            if (error.Length > 0)
            {
                return (false, error);
            }
        }

        // ComfyUI 没在跑 → 走内置推理：同一个解释器、同一份模型，但不启它的服务
        if (!env.Running)
        {
            return StartLocalJob(p);
        }

        try
        {
            string promptId = await _runner.SubmitAsync(p, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _job = new MossJobStatus
                {
                    Id = DateTime.Now.ToString("HHmmss"),
                    State = "submitted",
                    PromptId = promptId,
                    Message = "已提交，等引擎接手…",
                    Seed = p.Seed,
                    Steps = p.Steps,
                    SubmittedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                };
                _stopPoll = false;
            }

            _ = Task.Run(() => PollLoopAsync(promptId, p.Seed));
            return (true, "已提交（种子 " + p.Seed + "，步数 " + p.Steps + "）");
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is InvalidOperationException || ex is TaskCanceledException)
        {
            lock (_gate)
            {
                _job.State = "error";
                _job.Message = ex.Message;
            }

            return (false, ex.Message);
        }
    }

    /// <summary>按显存使用方式重启引擎（对应教程的按需放进显卡 / 逐层搬运 / 全部常驻）。</summary>
    /// <param name="mode">lowvram / normalvram / highvram（空 = 用配置里存的）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否就绪, 说明)。</returns>
    public async Task<(bool Ok, string Message)> RestartEngineAsync(string mode, CancellationToken ct)
    {
        string use = mode.Length > 0 ? mode : _config.VramMode;
        if (use.Length == 0)
        {
            use = "normalvram";
        }

        return await MossEngine.RestartAsync(_comfyRoot, _comfyUrl, use, ct).ConfigureAwait(false);
    }

    /// <summary>中断当前生成。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>说明。</returns>
    public async Task<string> InterruptAsync(CancellationToken ct)
    {
        _stopPoll = true;

        bool local;
        lock (_gate)
        {
            local = _job.PromptId == "local";
            if (local)
            {
                _job.State = "idle";
                _job.Message = "已停内置引擎（这次生成作废）";
                _job.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }
        }

        if (local)
        {
            _local.Stop();
            return "已停内置引擎（这次生成作废）";
        }

        try
        {
            await _runner.InterruptAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                _job.State = "idle";
                _job.Message = "已请求中断";
            }

            return "已请求中断";
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
        {
            return "中断失败：" + ex.Message;
        }
    }

    /// <summary>产物目录里的图片清单（新→旧）。</summary>
    /// <returns>文件名列表。</returns>
    public List<string> ListOutputs()
    {
        var list = new List<string>();
        try
        {
            if (!Directory.Exists(_outDir))
            {
                return list;
            }

            var files = new DirectoryInfo(_outDir).GetFiles("*.png");
            Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            foreach (FileInfo file in files)
            {
                list.Add(file.Name);
                if (list.Count >= 60)
                {
                    break;
                }
            }
        }
        catch (IOException)
        {
            return list;
        }

        return list;
    }

    /// <summary>产物图片的完整路径（面板按名字取图）。</summary>
    /// <param name="name">文件名。</param>
    /// <returns>完整路径；越界或不存在返回空串。</returns>
    public string OutputPath(string name)
    {
        if (name.Length == 0 || name.Contains("..", StringComparison.Ordinal) || name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
        {
            return "";
        }

        string path = Path.Combine(_outDir, name);
        return File.Exists(path) ? path : "";
    }

    /// <summary>把配置折算成节点图参数（含按方案解析模型名）。</summary>
    /// <param name="env">环境快照。</param>
    /// <param name="error">失败原因（成功时为空）。</param>
    /// <returns>参数对象。</returns>
    private MossGenParams BuildParams(MossEnvSnapshot env, out string error)
    {
        error = "";
        var p = new MossGenParams
        {
            Prompt = _config.Prompt,
            Negative = _config.Negative,
            Width = Align16(_config.Width),
            Height = Align16(_config.Height),
            Steps = _config.Accel ? 8 : _config.Steps,
            Cfg = _config.Accel ? 1.0 : _config.Cfg,
            Shift = _config.Shift,
            Batch = _config.Batch,
            Seed = _config.Seed >= 0 ? _config.Seed : Random.Shared.NextInt64(0, 4294967295),
            Accel = _config.Accel,
            AccelLora = _config.AccelLora,
            AccelStrength = _config.AccelStrength,
            Slg = _config.Slg,
            SlgLayers = _config.SlgLayers,
            LoraName = _config.LoraName,
            LoraStrength = _config.LoraStrength,
            Hires = _config.Hires,
            HiresScale = _config.HiresScale,
            HiresDenoise = _config.HiresDenoise,
            ImgDenoise = _config.ImgDenoise,
        };

        // 编辑模式：走编辑插件的两个节点——底模固定为 diffusers 快照，参考图先拷进 ComfyUI 的 input
        if (IsEditMode())
        {
            p.Mode = "edit";
            p.SnapshotDir = Path.Combine(_env.HfRoot, "MOSS-Image-SFT");
            p.AdapterDir = _env.EditAdapterRoot;
            // 勾了 8 步加速：DMD 由内置引擎并进底模（环境变量），步数压到 8；
            // CFG 用面板值——实测 CFG 1 细节略糊、CFG 4 会过曝（2026-10-22 三档对比）
            p.Steps = _config.Accel ? 8 : _config.Steps;
            p.Cfg = _config.Cfg;
            p.Shift = _config.Shift;
            p.RefMaxEdge = _config.RefMaxEdge;

            foreach (string image in _config.RefImages)
            {
                if (image.Length == 0)
                {
                    continue;
                }

                string staged = MossRunner.StageImage(image, _inputDir);
                if (staged.Length == 0)
                {
                    error = "参考图读不到：" + image;
                    return p;
                }

                p.RefImages.Add(staged);
            }

            if (p.RefImages.Count == 0)
            {
                error = "图像编辑至少要一张参考图（reference_1 必填）";
            }

            return p;
        }

        MossPlan? plan = MossCatalog.FindPlan(_config.Plan);
        if (plan is null)
        {
            error = "配置里的方案不存在：" + _config.Plan;
            return p;
        }

        if (string.Equals(plan.Id, "diffusers", StringComparison.OrdinalIgnoreCase))
        {
            p.Mode = "hf";
            p.HfPath = Path.Combine(_env.HfRoot, "MOSS-Image-SFT");
            if (!Directory.Exists(p.HfPath))
            {
                error = "官方完整目录没下齐：" + p.HfPath;
            }
        }
        else
        {
            p.Mode = "single";
            string unet = NameOf(plan, MossTarget.DiffusionModels);
            string clip = NameOf(plan, MossTarget.TextEncoders);
            string vae = NameOf(plan, MossTarget.Vae);
            if (unet.Length == 0 || clip.Length == 0 || vae.Length == 0)
            {
                error = "方案 " + plan.Title + " 的索引不完整";
                return p;
            }

            p.UnetName = unet;
            p.ClipName = clip;
            p.VaeName = vae;

            string unetPath = Path.Combine(_env.ModelsRoot, "diffusion_models", unet);
            string clipPath = Path.Combine(_env.ModelsRoot, "text_encoders", clip);
            string vaePath = Path.Combine(_env.ModelsRoot, "vae", vae);
            if (!File.Exists(unetPath))
            {
                error = "缺主模型文件：" + unetPath;
            }
            else if (!File.Exists(clipPath))
            {
                error = "缺文本编码器：" + clipPath;
            }
            else if (!File.Exists(vaePath))
            {
                error = "缺 VAE：" + vaePath;
            }
        }

        if (_config.Accel && !File.Exists(Path.Combine(_env.ModelsRoot, "loras", "MOSS", "MOSS-SFT-8step-DMD.safetensors")))
        {
            error = "开了 8 步加速但加速 LoRA 不在位——先点「下载加速 LoRA」";
        }

        if (_config.InputImage.Length > 0)
        {
            string staged = MossRunner.StageImage(_config.InputImage, _inputDir);
            if (staged.Length == 0)
            {
                error = "图生图的输入图片读不到：" + _config.InputImage;
            }
            else
            {
                p.InputImage = staged;
            }
        }

        return p;
    }

    /// <summary>当前配置是不是图像编辑模式。</summary>
    /// <returns>编辑模式返回 true。</returns>
    private bool IsEditMode()
    {
        return _config.RefImages.Count > 0;
    }

    /// <summary>方案里某分类的文件名（ComfyUI 侧用的相对名）。</summary>
    /// <param name="plan">方案。</param>
    /// <param name="target">分类。</param>
    /// <returns>文件名；没有返回空串。</returns>
    private static string NameOf(MossPlan plan, MossTarget target)
    {
        foreach (string id in plan.Files)
        {
            MossFile? file = MossCatalog.Find(id);
            if (file is not null && file.Target == target)
            {
                return file.ResolvedFileName();
            }
        }

        return "";
    }

    /// <summary>轮询 ComfyUI 直到出图或报错。</summary>
    /// <param name="promptId">引擎侧任务号。</param>
    /// <param name="seed">本次种子。</param>
    /// <returns>无。</returns>
    private async Task PollLoopAsync(string promptId, long seed)
    {
        for (int i = 0; i < 7200; i++)
        {
            if (_stopPoll)
            {
                return;
            }

            await Task.Delay(1500).ConfigureAwait(false);
            if (_stopPoll)
            {
                return;
            }

            string state;
            List<(string File, string Sub, string Type)> images;
            string message;
            try
            {
                (state, images, message) = await _runner.PollAsync(promptId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException)
            {
                lock (_gate)
                {
                    _job.State = "error";
                    _job.Message = "查任务失败：" + ex.Message;
                    _job.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                }

                return;
            }

            lock (_gate)
            {
                if (_job.Message != message && state == "running")
                {
                    _job.Message = message;
                    _job.State = "running";
                }
            }

            if (state == "error")
            {
                lock (_gate)
                {
                    _job.State = "error";
                    _job.Message = message;
                    _job.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                }

                return;
            }

            if (state != "done")
            {
                continue;
            }

            List<string> saved;
            try
            {
                saved = await _runner.FetchAsync(images, _outDir, seed, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is TaskCanceledException)
            {
                lock (_gate)
                {
                    _job.State = "error";
                    _job.Message = "取图失败：" + ex.Message;
                    _job.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                }

                return;
            }

            lock (_gate)
            {
                _job.State = "done";
                _job.Images = saved;
                _job.Message = "出图 " + saved.Count + " 张（" + _outDir + "）";
                _job.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }

            return;
        }

        lock (_gate)
        {
            _job.State = "error";
            _job.Message = "等太久（3 小时）没等到结果——去 ComfyUI 控制台看";
        }
    }

    /// <summary>扫一个模型目录下的文件（面板下拉框用）。</summary>
    /// <param name="sub">模型子目录。</param>
    /// <param name="pattern">文件模式。</param>
    /// <returns>相对名列表（用 / 分隔，ComfyUI 认这个）。</returns>
    private List<string> ListModels(string sub, string pattern)
    {
        var list = new List<string>();
        try
        {
            string root = Path.Combine(_env.ModelsRoot, sub);
            if (!Directory.Exists(root))
            {
                return list;
            }

            foreach (string path in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                list.Add(rel);
                if (list.Count >= 400)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return list;
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>读配置（坏文件回落默认值，不阻断启动）。</summary>
    /// <returns>配置。</returns>
    private MossConfig LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                MossConfig? loaded = JsonSerializer.Deserialize<MossConfig>(File.ReadAllText(_configPath), options);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException)
        {
            return new MossConfig();
        }

        return new MossConfig();
    }

    /// <summary>落盘配置。</summary>
    private void SaveConfig()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_configPath);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            };
            File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, options));
        }
        catch (IOException ex)
        {
            // 配置落盘失败不该打断生成——面板下次仍用默认值
            Console.Error.WriteLine("MOSS 配置落盘失败：" + ex.Message);
        }
    }

    /// <summary>宽高对齐到 16 的倍数（MOSS 硬性要求）。</summary>
    /// <param name="value">原始值。</param>
    /// <returns>对齐值。</returns>
    private static int Align16(int value)
    {
        int aligned = (value / 16) * 16;
        return aligned < 256 ? 256 : aligned;
    }

    /// <summary>夹取数值范围。</summary>
    /// <param name="value">值。</param>
    /// <param name="min">下限。</param>
    /// <param name="max">上限。</param>
    /// <returns>夹取后的值。</returns>
    private static int Clamp(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    /// <summary>夹取数值范围。</summary>
    /// <param name="value">值。</param>
    /// <param name="min">下限。</param>
    /// <param name="max">上限。</param>
    /// <returns>夹取后的值。</returns>
    private static double Clamp(double value, double min, double max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }
    /// <summary>内置引擎状态（面板显示「不用开 ComfyUI」这一路的死活）。</summary>
    public MossLocalState Local
    {
        get { return _local.State; }
    }
    /// <summary>把这次生成交给内置引擎——不启 ComfyUI 服务，用它的解释器与模型直接跑。</summary>
    /// <param name="p">生成参数。</param>
    /// <returns>(是否已受理, 说明)。</returns>
    private (bool Ok, string Message) StartLocalJob(MossGenParams p)
    {
        lock (_gate)
        {
            _job = new MossJobStatus
            {
                Id = DateTime.Now.ToString("HHmmss"),
                State = "running",
                PromptId = "local",
                Message = "内置引擎跑图中（不用开 ComfyUI）…",
                Seed = p.Seed,
                Steps = p.Steps,
                SubmittedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };
            _stopPoll = false;
        }

        _ = Task.Run(() => RunLocalJobAsync(p));
        return (true, "已交给内置引擎（种子 " + p.Seed + "，步数 " + p.Steps + "）");
    }
    /// <summary>内置引擎跑一次，结束后更新任务状态。</summary>
    /// <param name="p">生成参数。</param>
    /// <returns>无。</returns>
    private async Task RunLocalJobAsync(MossGenParams p)
    {
        // 编辑 + 8 步加速：让 worker 把 DMD 并进底模（环境变量只在 worker 启动时读，值变了会重启 worker）
        if (string.Equals(p.Mode, "edit", StringComparison.OrdinalIgnoreCase) && p.Accel)
        {
            _local.SetExtraLora(Path.Combine(_env.ModelsRoot, "loras", "MOSS", "MOSS-SFT-8step-DMD.safetensors"));
        }
        else
        {
            _local.SetExtraLora("");
        }

        // 面板里 LoRA 名用正斜杠，ComfyUI 认反斜杠（内置通道问不到引擎的选项表，直接按规则换）
        p.AccelLora = p.AccelLora.Replace('/', '\\');
        p.LoraName = p.LoraName.Replace('/', '\\');

        JsonObject graph = MossRunner.BuildGraph(p);
        (bool ok, List<string> files, string message) = await _local
            .RunAsync(graph, _outDir, p.Seed, CancellationToken.None)
            .ConfigureAwait(false);

        lock (_gate)
        {
            _job.State = ok ? "done" : "error";
            _job.Images = files;
            _job.Message = message;
            _job.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }
    /// <summary>停掉内置引擎（把显存还回去）。</summary>
    /// <returns>说明。</returns>
    public string StopLocal()
    {
        _local.Stop();
        return "内置引擎已停止";
    }
}
