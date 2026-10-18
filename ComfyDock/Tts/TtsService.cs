using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Tts;

/// <summary>
/// 本地生成（依田芳乃 TTS）的持久化配置——生成参数 + 这一路要用的路径（路径可在面板里改，落盘 <c>data\tts-config.json</c>）。
/// </summary>
public sealed class TtsConfig
{
    /// <summary>要合成的文本。</summary>
    public string Text { get; set; } = "";

    /// <summary>风格描述（voice design 条件，空 = 不用）。</summary>
    public string Caption { get; set; } = "";

    /// <summary>选中的参考音频文件名（相对参考目录；空 = 用目录里全部）。</summary>
    public List<string> Refs { get; set; } = new List<string>();

    /// <summary>不用参考音频（音色会漂，一般关着）。</summary>
    public bool NoRef { get; set; }

    /// <summary>采样步数（默认 40）。</summary>
    public int Steps { get; set; } = 40;

    /// <summary>种子（-1 = 每次随机）。</summary>
    public long Seed { get; set; } = -1;

    /// <summary>时长倍率（&gt;1 更长）。</summary>
    public double DurationScale { get; set; } = 1.0;

    /// <summary>强制时长（秒，0 = 由模型自动预测）。</summary>
    public double Seconds { get; set; }

    /// <summary>模型精度（bf16 / fp32）。</summary>
    public string Precision { get; set; } = "bf16";

    /// <summary>跑法：small（小模型全 GPU）/ large-int8（大模型 int8 量化，8 G 卡推荐）/ large（大模型 bf16·声码器挪 CPU）/ large-gpu（大模型 bf16 全 GPU）/ custom（自定义）。</summary>
    public string RunMode { get; set; } = "small";

    /// <summary>声码器设备（cuda / cpu）——custom 跑法直接用它，其余跑法由跑法决定。</summary>
    public string CodecDevice { get; set; } = "cuda";

    /// <summary>大模型基座 safetensors（v4-Large，3.29B，fp32 存盘 13.2 G）。</summary>
    public string LargeBaseModel { get; set; } = "";

    /// <summary>大模型 int8 量化基座 safetensors（v4-Large-Quantized/int8-weight-only，3.84 G）。</summary>
    public string LargeInt8BaseModel { get; set; } = "";

    /// <summary>大模型 LoRA 适配器目录（rank 32）。</summary>
    public string LargeLoraDir { get; set; } = "";

    /// <summary>推理设备（cuda / cpu）。</summary>
    public string Device { get; set; } = "cuda";

    /// <summary>是否挂 LoRA（关掉 = 只用基座，用于对比）。</summary>
    public bool UseLora { get; set; } = true;

    /// <summary>venv 解释器（irodori-tts 自己的环境）。</summary>
    public string PythonPath { get; set; } = "";

    /// <summary>Irodori-TTS 仓库根目录。</summary>
    public string RepoDir { get; set; } = "";

    /// <summary>基座 safetensors。</summary>
    public string BaseModel { get; set; } = "";

    /// <summary>声码器 weights.pth。</summary>
    public string CodecWeights { get; set; } = "";

    /// <summary>LoRA 适配器目录。</summary>
    public string LoraDir { get; set; } = "";

    /// <summary>参考音频目录。</summary>
    public string RefsDir { get; set; } = "";
}

/// <summary>
/// 「怎么个跑法」——把小模型 / 大模型两条线的路径与设备按跑法解析成一套生效参数。
/// <para>跑法决定基座、LoRA、精度与声码器设备；面板上的路径字段只做覆盖来源，避免同一条链路两处真相。</para>
/// </summary>
public sealed class TtsPlan
{
    /// <summary>生效的基座 safetensors。</summary>
    public string BaseModel { get; set; } = "";

    /// <summary>生效的 LoRA 目录（空 = 不挂）。</summary>
    public string LoraDir { get; set; } = "";

    /// <summary>生效的模型精度。</summary>
    public string Precision { get; set; } = "bf16";

    /// <summary>生效的模型设备。</summary>
    public string Device { get; set; } = "cuda";

    /// <summary>生效的声码器设备。</summary>
    public string CodecDevice { get; set; } = "cuda";

    /// <summary>跑法人话名（面板显示）。</summary>
    public string Label { get; set; } = "";

    /// <summary>按跑法解析出这一代 worker 的生效参数。</summary>
    /// <param name="cfg">当前配置。</param>
    /// <returns>生效参数。</returns>
    public static TtsPlan Resolve(TtsConfig cfg)
    {
        string mode = (cfg.RunMode ?? "").Trim().ToLowerInvariant();
        string device = (cfg.Device ?? "").Trim();
        if (device.Length == 0)
        {
            device = "cuda";
        }

        string precision = (cfg.Precision ?? "").Trim();
        if (precision.Length == 0)
        {
            precision = "bf16";
        }

        string lora = cfg.UseLora ? (cfg.LoraDir ?? "") : "";

        if (mode == "large-int8")
        {
            // 3.29B 的 int8 权重只有 3.84 G，8 G 卡上连声码器都能留在 GPU，且不用共享显存拖尾
            return new TtsPlan
            {
                BaseModel = cfg.LargeInt8BaseModel ?? "",
                LoraDir = cfg.UseLora ? (cfg.LargeLoraDir ?? "") : "",
                Precision = "bf16",
                Device = device,
                CodecDevice = device,
                Label = "大模型 int8 量化 · 全 GPU（8 G 卡推荐）",
            };
        }

        if (mode == "large" || mode == "large-gpu")
        {
            // 大模型 3.29B：bf16 约 6.6 G 显存，8 G 卡上必须把声码器挪到 CPU 才留得出余量
            return new TtsPlan
            {
                BaseModel = cfg.LargeBaseModel ?? "",
                LoraDir = cfg.UseLora ? (cfg.LargeLoraDir ?? "") : "",
                Precision = "bf16",
                Device = device,
                CodecDevice = mode == "large" ? "cpu" : device,
                Label = mode == "large" ? "大模型 v4-Large · 声码器在 CPU" : "大模型 v4-Large · 全 GPU",
            };
        }

        if (mode == "custom")
        {
            string codecDevice = (cfg.CodecDevice ?? "").Trim();
            return new TtsPlan
            {
                BaseModel = cfg.BaseModel ?? "",
                LoraDir = lora,
                Precision = precision,
                Device = device,
                CodecDevice = codecDevice.Length == 0 ? device : codecDevice,
                Label = "自定义（按下面填的路径与设备）",
            };
        }

        return new TtsPlan
        {
            BaseModel = cfg.BaseModel ?? "",
            LoraDir = lora,
            Precision = precision,
            Device = device,
            CodecDevice = device,
            Label = "小模型 v4.1-Small · 全 GPU",
        };
    }
}

/// <summary>
/// 一次合成任务的状态（面板轮询它）。
/// </summary>
public sealed class TtsJobStatus
{
    /// <summary>任务号。</summary>
    public string Id { get; set; } = "";

    /// <summary>状态：idle / submitted / running / done / error。</summary>
    public string State { get; set; } = "idle";

    /// <summary>人话说明。</summary>
    public string Message { get; set; } = "";

    /// <summary>文本前 40 字（面板回显）。</summary>
    public string TextPreview { get; set; } = "";

    /// <summary>产物文件名。</summary>
    public string File { get; set; } = "";

    /// <summary>产物音频秒数。</summary>
    public double AudioSeconds { get; set; }

    /// <summary>合成耗时（秒）。</summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>实际种子。</summary>
    public long Seed { get; set; }

    /// <summary>提交时刻。</summary>
    public string SubmittedAt { get; set; } = "";

    /// <summary>结束时刻。</summary>
    public string FinishedAt { get; set; } = "";
}

/// <summary>
/// 本机 TTS 环境实况——解释器 / 仓库 / 基座 / 声码器 / LoRA / 参考音频在不在。全部来自磁盘实测。
/// </summary>
public sealed class TtsEnvSnapshot
{
    /// <summary>解释器在位。</summary>
    public bool PythonOk { get; set; }

    /// <summary>仓库目录在位。</summary>
    public bool RepoOk { get; set; }

    /// <summary>基座在位。</summary>
    public bool BaseModelOk { get; set; }

    /// <summary>声码器在位。</summary>
    public bool CodecOk { get; set; }

    /// <summary>LoRA 目录在位（含 adapter_config.json）。</summary>
    public bool LoraOk { get; set; }

    /// <summary>参考音频个数。</summary>
    public int RefCount { get; set; }

    /// <summary>能不能生成。</summary>
    public bool CanGenerate { get; set; }

    /// <summary>缺什么（人话，逐条）。</summary>
    public List<string> Blockers { get; set; } = new List<string>();

    /// <summary>探测时刻。</summary>
    public string CheckedAt { get; set; } = "";
}

/// <summary>
/// 本地生成板块（依田芳乃 TTS）的服务面——环境探针 + 配置 + 常驻引擎投递 + 产物清单。
/// <para>与 MOSS-Image 平行：那边走 ComfyUI，这边走 irodori-tts 自己的 venv（常驻 worker，模型只加载一次）。</para>
/// </summary>
public sealed class TtsService
{
    /// <summary>配置落盘路径。</summary>
    private readonly string _configPath;

    /// <summary>产物落点目录。</summary>
    private readonly string _outDir;

    /// <summary>状态锁。</summary>
    private readonly object _gate = new object();

    /// <summary>常驻引擎。</summary>
    private readonly IrodoriTtsEngine _engine = new IrodoriTtsEngine();

    /// <summary>当前配置。</summary>
    private TtsConfig _config;

    /// <summary>当前任务状态。</summary>
    private TtsJobStatus _job = new TtsJobStatus();

    /// <summary>以选项构造。</summary>
    /// <param name="defaults">默认配置（路径来自命令行选项）。</param>
    /// <param name="dataDir">数据目录（配置落点）。</param>
    /// <param name="outDir">产物落点目录。</param>
    public TtsService(TtsConfig defaults, string dataDir, string outDir)
    {
        _configPath = Path.Combine(dataDir, "tts-config.json");
        _outDir = outDir;
        _config = LoadConfig();

        // 落盘配置里没写的路径，用命令行选项的默认值补上（面板可再改）
        if (_config.PythonPath.Length == 0)
        {
            _config.PythonPath = defaults.PythonPath;
        }

        if (_config.RepoDir.Length == 0)
        {
            _config.RepoDir = defaults.RepoDir;
        }

        if (_config.BaseModel.Length == 0)
        {
            _config.BaseModel = defaults.BaseModel;
        }

        if (_config.CodecWeights.Length == 0)
        {
            _config.CodecWeights = defaults.CodecWeights;
        }

        if (_config.LoraDir.Length == 0)
        {
            _config.LoraDir = defaults.LoraDir;
        }

        if (_config.RefsDir.Length == 0)
        {
            _config.RefsDir = defaults.RefsDir;
        }

        if (_config.LargeBaseModel.Length == 0)
        {
            _config.LargeBaseModel = defaults.LargeBaseModel;
        }

        if ((_config.LargeInt8BaseModel ?? "").Length == 0)
        {
            _config.LargeInt8BaseModel = defaults.LargeInt8BaseModel;
        }

        if (_config.LargeLoraDir.Length == 0)
        {
            _config.LargeLoraDir = defaults.LargeLoraDir;
        }

        if ((_config.RunMode ?? "").Length == 0)
        {
            _config.RunMode = defaults.RunMode;
        }

        ApplyEnginePaths();
    }

    /// <summary>产物落点目录。</summary>
    public string OutDir
    {
        get { return _outDir; }
    }

    /// <summary>常驻引擎状态（面板显示这一路的死活）。</summary>
    public TtsEngineState Engine
    {
        get { return _engine.State; }
    }

    /// <summary>当前任务状态（拷贝，防外部改）。</summary>
    /// <returns>任务状态副本。</returns>
    public TtsJobStatus Job()
    {
        lock (_gate)
        {
            return new TtsJobStatus
            {
                Id = _job.Id,
                State = _job.State,
                Message = _job.Message,
                TextPreview = _job.TextPreview,
                File = _job.File,
                AudioSeconds = _job.AudioSeconds,
                ElapsedSeconds = _job.ElapsedSeconds,
                Seed = _job.Seed,
                SubmittedAt = _job.SubmittedAt,
                FinishedAt = _job.FinishedAt,
            };
        }
    }

    /// <summary>面板快照——环境 + 配置 + 任务 + 引擎 + 产物 + 参考音频。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>快照对象（匿名结构，直接序列化给前端）。</returns>
    public Task<object> SnapshotAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TtsEnvSnapshot env = Probe();
        TtsPlan plan = TtsPlan.Resolve(_config);

        object snapshot = new
        {
            env = new
            {
                pythonOk = env.PythonOk,
                repoOk = env.RepoOk,
                baseModelOk = env.BaseModelOk,
                codecOk = env.CodecOk,
                loraOk = env.LoraOk,
                refCount = env.RefCount,
                canGenerate = env.CanGenerate,
                blockers = env.Blockers,
                checkedAt = env.CheckedAt,
            },
            config = _config,
            plan = new
            {
                label = plan.Label,
                baseModel = plan.BaseModel,
                loraDir = plan.LoraDir,
                precision = plan.Precision,
                device = plan.Device,
                codecDevice = plan.CodecDevice,
            },
            job = Job(),
            engine = Engine,
            refs = ListRefs(),
            outputs = ListOutputs(),
            outDir = _outDir,
        };

        return Task.FromResult(snapshot);
    }

    /// <summary>改配置（传进来的非空字段覆盖，其余不动）。</summary>
    /// <param name="next">新配置。</param>
    /// <returns>落盘后的配置。</returns>
    public TtsConfig UpdateConfig(TtsConfig next)
    {
        lock (_gate)
        {
            _config.Text = next.Text ?? "";
            _config.Caption = next.Caption ?? "";
            _config.Refs = next.Refs ?? new List<string>();
            _config.NoRef = next.NoRef;
            _config.Steps = Clamp(next.Steps, 1, 200);
            _config.Seed = next.Seed;
            _config.DurationScale = Clamp(next.DurationScale, 0.2, 4.0);
            _config.Seconds = Clamp(next.Seconds, 0.0, 600.0);
            _config.Precision = next.Precision == "fp32" ? "fp32" : "bf16";
            _config.Device = string.IsNullOrEmpty(next.Device) ? "cuda" : next.Device;
            _config.UseLora = next.UseLora;

            string mode = (next.RunMode ?? "").Trim().ToLowerInvariant();
            _config.RunMode = mode == "large" || mode == "large-gpu" || mode == "large-int8" || mode == "custom"
                ? mode
                : "small";
            _config.CodecDevice = next.CodecDevice == "cpu" ? "cpu" : "cuda";

            if (next.LargeBaseModel.Length > 0)
            {
                _config.LargeBaseModel = next.LargeBaseModel;
            }

            if (next.LargeInt8BaseModel.Length > 0)
            {
                _config.LargeInt8BaseModel = next.LargeInt8BaseModel;
            }

            if (next.LargeLoraDir.Length > 0)
            {
                _config.LargeLoraDir = next.LargeLoraDir;
            }

            if (next.PythonPath.Length > 0)
            {
                _config.PythonPath = next.PythonPath;
            }

            if (next.RepoDir.Length > 0)
            {
                _config.RepoDir = next.RepoDir;
            }

            if (next.BaseModel.Length > 0)
            {
                _config.BaseModel = next.BaseModel;
            }

            if (next.CodecWeights.Length > 0)
            {
                _config.CodecWeights = next.CodecWeights;
            }

            _config.LoraDir = next.LoraDir ?? "";
            _config.RefsDir = next.RefsDir.Length > 0 ? next.RefsDir : _config.RefsDir;
            SaveConfig();
        }

        // 路径可能变了——把在跑的引擎按新参数重启（Prepare 内部会判断是否真的变了）
        ApplyEnginePaths();
        return _config;
    }

    /// <summary>按当前配置投递一次合成（立刻返回，跑在后台，面板轮询状态）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否受理, 说明)。</returns>
    public Task<(bool Ok, string Message)> GenerateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        TtsEnvSnapshot env = Probe();
        if (!env.CanGenerate)
        {
            string reason = env.Blockers.Count > 0 ? env.Blockers[0] : "前置条件不足";
            return Task.FromResult((false, reason));
        }

        string text;
        TtsConfig snapshot;
        lock (_gate)
        {
            if (_job.State == "submitted" || _job.State == "running")
            {
                return Task.FromResult((false, "上一句还在跑——先等它出来，或点「停止引擎」"));
            }

            text = (_config.Text ?? "").Trim();
            if (text.Length == 0)
            {
                return Task.FromResult((false, "文本为空"));
            }

            snapshot = new TtsConfig
            {
                Text = text,
                Caption = _config.Caption ?? "",
                Refs = new List<string>(_config.Refs ?? new List<string>()),
                NoRef = _config.NoRef,
                Steps = _config.Steps,
                Seed = _config.Seed,
                DurationScale = _config.DurationScale,
                Seconds = _config.Seconds,
                Precision = _config.Precision,
                Device = _config.Device,
                UseLora = _config.UseLora,
                RunMode = _config.RunMode,
                CodecDevice = _config.CodecDevice,
                LargeBaseModel = _config.LargeBaseModel,
                LargeLoraDir = _config.LargeLoraDir,
                PythonPath = _config.PythonPath,
                RepoDir = _config.RepoDir,
                BaseModel = _config.BaseModel,
                CodecWeights = _config.CodecWeights,
                LoraDir = _config.LoraDir,
                RefsDir = _config.RefsDir,
            };

            _job = new TtsJobStatus
            {
                Id = DateTime.Now.ToString("HHmmss"),
                State = "submitted",
                Message = "已提交，等引擎接手…",
                TextPreview = Preview(text),
                Seed = snapshot.Seed,
                SubmittedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };
        }

        _ = Task.Run(() => RunJobAsync(snapshot));
        return Task.FromResult((true, "已提交（步数 " + snapshot.Steps + "，种子 "
            + (snapshot.Seed < 0 ? "随机" : snapshot.Seed.ToString()) + "）"));
    }

    /// <summary>停掉常驻引擎（把显存还回去；在跑的这一句作废）。</summary>
    /// <returns>说明。</returns>
    public string Stop()
    {
        _engine.Stop();
        lock (_gate)
        {
            if (_job.State == "submitted" || _job.State == "running")
            {
                _job.State = "idle";
                _job.Message = "已停引擎（这次合成作废）";
                _job.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }
        }

        return "已停引擎";
    }

    /// <summary>产物目录里的音频清单（新→旧）。</summary>
    /// <returns>文件名列表。</returns>
    public List<object> ListOutputs()
    {
        var list = new List<object>();
        try
        {
            if (!Directory.Exists(_outDir))
            {
                return list;
            }

            var files = new DirectoryInfo(_outDir).GetFiles("*.wav");
            Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            foreach (FileInfo file in files)
            {
                list.Add(new
                {
                    name = file.Name,
                    bytes = file.Length,
                    at = file.LastWriteTime.ToString("MM-dd HH:mm:ss"),
                });

                if (list.Count >= 40)
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

    /// <summary>参考音频目录里的清单（面板勾选用）。</summary>
    /// <returns>文件名列表。</returns>
    public List<object> ListRefs()
    {
        var list = new List<object>();
        string dir = _config.RefsDir;
        if (dir.Length == 0 || !Directory.Exists(dir))
        {
            return list;
        }

        string[] patterns = { "*.mp3", "*.wav", "*.flac", "*.m4a", "*.ogg" };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pattern in patterns)
        {
            try
            {
                foreach (FileInfo file in new DirectoryInfo(dir).GetFiles(pattern))
                {
                    if (!seen.Add(file.Name))
                    {
                        continue;
                    }

                    list.Add(new
                    {
                        name = file.Name,
                        seconds = 0.0,
                        bytes = file.Length,
                    });
                }
            }
            catch (IOException)
            {
                return list;
            }
        }

        return list;
    }

    /// <summary>产物音频的完整路径（面板播放用）。</summary>
    /// <param name="name">文件名。</param>
    /// <returns>完整路径；越界或不存在返回空串。</returns>
    public string OutputPath(string name)
    {
        if (name.Length == 0
            || name.Contains("..", StringComparison.Ordinal)
            || name.Contains('/', StringComparison.Ordinal)
            || name.Contains('\\', StringComparison.Ordinal))
        {
            return "";
        }

        string path = Path.Combine(_outDir, name);
        return File.Exists(path) ? path : "";
    }

    /// <summary>环境探针（只看磁盘，不启动进程）。</summary>
    /// <returns>环境快照。</returns>
    private TtsEnvSnapshot Probe()
    {
        TtsConfig cfg;
        lock (_gate)
        {
            cfg = _config;
        }

        var blockers = new List<string>();
        TtsPlan plan = TtsPlan.Resolve(cfg);
        bool pythonOk = cfg.PythonPath.Length > 0 && File.Exists(cfg.PythonPath);
        if (!pythonOk)
        {
            blockers.Add("解释器不在位：" + cfg.PythonPath);
        }

        bool repoOk = cfg.RepoDir.Length > 0 && File.Exists(Path.Combine(cfg.RepoDir, "infer.py"));
        if (!repoOk)
        {
            blockers.Add("Irodori-TTS 仓库不完整（缺 infer.py）：" + cfg.RepoDir);
        }

        bool baseOk = plan.BaseModel.Length > 0 && File.Exists(plan.BaseModel);
        if (!baseOk)
        {
            blockers.Add("基座模型不在位（跑法「" + plan.Label + "」）：" + plan.BaseModel);
        }

        bool codecOk = cfg.CodecWeights.Length > 0 && File.Exists(cfg.CodecWeights);
        if (!codecOk)
        {
            blockers.Add("声码器权重不在位：" + cfg.CodecWeights);
        }

        bool loraOk = plan.LoraDir.Length > 0
            && File.Exists(Path.Combine(plan.LoraDir, "adapter_config.json"))
            && File.Exists(Path.Combine(plan.LoraDir, "adapter_model.safetensors"));
        if (!loraOk && cfg.UseLora)
        {
            blockers.Add("LoRA 适配器不在位（或没 adapter_config.json）：" + plan.LoraDir);
        }

        int refCount = 0;
        if (cfg.RefsDir.Length > 0 && Directory.Exists(cfg.RefsDir))
        {
            string[] patterns = { "*.mp3", "*.wav", "*.flac", "*.m4a", "*.ogg" };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string pattern in patterns)
            {
                try
                {
                    foreach (FileInfo file in new DirectoryInfo(cfg.RefsDir).GetFiles(pattern))
                    {
                        if (seen.Add(file.Name))
                        {
                            refCount++;
                        }
                    }
                }
                catch (IOException)
                {
                    break;
                }
            }
        }

        if (refCount == 0 && !cfg.NoRef)
        {
            blockers.Add("参考音频目录里没有音频：" + cfg.RefsDir);
        }

        return new TtsEnvSnapshot
        {
            PythonOk = pythonOk,
            RepoOk = repoOk,
            BaseModelOk = baseOk,
            CodecOk = codecOk,
            LoraOk = loraOk,
            RefCount = refCount,
            CanGenerate = blockers.Count == 0,
            Blockers = blockers,
            CheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };
    }

    /// <summary>把配置里的路径推给常驻引擎（参数没变就什么都不做）。</summary>
    private void ApplyEnginePaths()
    {
        TtsPlan plan = TtsPlan.Resolve(_config);
        _engine.Prepare(
            _config.PythonPath,
            _config.RepoDir,
            plan.BaseModel,
            _config.CodecWeights,
            plan.LoraDir,
            plan.Device,
            plan.Precision,
            plan.CodecDevice);
    }

    /// <summary>后台跑一次合成，结束后更新任务状态。</summary>
    /// <param name="cfg">本次参数快照（跑的过程中改面板不影响这一句）。</param>
    /// <returns>无。</returns>
    private async Task RunJobAsync(TtsConfig cfg)
    {
        var refs = new List<string>();
        if (!cfg.NoRef)
        {
            string dir = cfg.RefsDir;
            var picked = cfg.Refs ?? new List<string>();
            if (dir.Length > 0 && Directory.Exists(dir))
            {
                if (picked.Count > 0)
                {
                    foreach (string name in picked)
                    {
                        string path = Path.Combine(dir, name);
                        if (File.Exists(path))
                        {
                            refs.Add(path);
                        }
                    }
                }
                else
                {
                    string[] patterns = { "*.mp3", "*.wav", "*.flac", "*.m4a", "*.ogg" };
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string pattern in patterns)
                    {
                        foreach (FileInfo info in new DirectoryInfo(dir).GetFiles(pattern))
                        {
                            if (seen.Add(info.FullName))
                            {
                                refs.Add(info.FullName);
                            }
                        }
                    }

                    refs.Sort(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string outPath = Path.Combine(_outDir, "yoshino_" + stamp + ".wav");
        try
        {
            Directory.CreateDirectory(_outDir);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            lock (_gate)
            {
                _job.State = "error";
                _job.Message = "产物目录不可写：" + ex.Message;
                _job.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }

            return;
        }

        lock (_gate)
        {
            _job.State = "running";
            _job.Message = "加载引擎 / 合成中…";
        }

        DateTime started = DateTime.Now;
        (bool ok, string file, double audioSeconds, long seed, string message) = await _engine
            .RunAsync(
                cfg.Text,
                cfg.Caption,
                refs,
                cfg.NoRef || refs.Count == 0,
                cfg.Steps,
                cfg.Seed,
                cfg.DurationScale,
                cfg.Seconds,
                cfg.UseLora,
                outPath,
                CancellationToken.None)
            .ConfigureAwait(false);

        double elapsed = (DateTime.Now - started).TotalSeconds;
        lock (_gate)
        {
            _job.State = ok ? "done" : "error";
            _job.Message = message;
            _job.File = file;
            _job.AudioSeconds = audioSeconds;
            _job.ElapsedSeconds = Math.Round(elapsed, 1);
            if (ok)
            {
                _job.Seed = seed;
            }

            _job.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }

    /// <summary>读配置（坏文件回落默认值，不阻断启动）。</summary>
    /// <returns>配置。</returns>
    private TtsConfig LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                TtsConfig? loaded = JsonSerializer.Deserialize<TtsConfig>(File.ReadAllText(_configPath), options);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException)
        {
            return new TtsConfig();
        }

        return new TtsConfig();
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
            Console.Error.WriteLine("TTS 配置落盘失败：" + ex.Message);
        }
    }

    /// <summary>文本前 40 字（面板回显用）。</summary>
    /// <param name="text">全文。</param>
    /// <returns>摘要。</returns>
    private static string Preview(string text)
    {
        string one = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return one.Length <= 40 ? one : one.Substring(0, 40) + "…";
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
}
