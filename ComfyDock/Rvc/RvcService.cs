using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Rvc;

/// <summary>
/// 本地生成（RVC 变声）的持久化配置——变声参数 + 这一路要用的路径（落盘 <c>data\rvc-config.json</c>）。
/// </summary>
public sealed class RvcConfig
{
    /// <summary>音色模型文件名（weights 目录下的 .pth）。</summary>
    public string Model { get; set; } = "";

    /// <summary>待变声的输入音频（完整路径）。</summary>
    public string InputPath { get; set; } = "";

    /// <summary>说话人 id（模型内置音色编号，多数模型只有 0）。</summary>
    public int Sid { get; set; }

    /// <summary>变调半音数（男→女约 +12，女→男约 -12）。</summary>
    public double F0UpKey { get; set; }

    /// <summary>F0 取法：rmvpe / pm / harvest / crepe。</summary>
    public string F0Method { get; set; } = "rmvpe";

    /// <summary>检索混合比例（0 = 不挂索引；越高越贴训练音色、咬字略糊）。</summary>
    public double IndexRate { get; set; } = 0.75;

    /// <summary>中值滤波半径（harvest 用，0~7）。</summary>
    public int FilterRadius { get; set; } = 3;

    /// <summary>输出重采样率（0 = 用模型自带采样率）。</summary>
    public int ResampleSr { get; set; }

    /// <summary>响度混合比例（0 = 全用输出响度；1 = 全用输入响度）。</summary>
    public double RmsMixRate { get; set; } = 0.25;

    /// <summary>清辅音保护（0.5 = 关闭；越小保护越强，防换气声被吃）。</summary>
    public double Protect { get; set; } = 0.33;

    /// <summary>推理设备（cuda / cpu）。</summary>
    public string Device { get; set; } = "cuda";

    /// <summary>半精度（显存吃紧时开；CPU 上自动关）。</summary>
    public bool Half { get; set; } = true;

    /// <summary>解释器（本机 ComfyUI 的 python_embeded\python.exe）。</summary>
    public string PythonPath { get; set; } = "";

    /// <summary>rvc_lite 包的上级目录（推理代码落点）。</summary>
    public string RepoDir { get; set; } = "";

    /// <summary>模型根目录（ComfyUI\models\RVC，下含 weights / indices / hubert / rmvpe）。</summary>
    public string ModelsRoot { get; set; } = "";

    /// <summary>批量变声的音色清单（多选；空 = 只用 <see cref="Model"/> 那一个）。</summary>
    public List<string> Models { get; set; } = new List<string>();
}

/// <summary>
/// 一次变声任务的状态（面板轮询它）。
/// </summary>
public sealed class RvcJobStatus
{
    /// <summary>任务号。</summary>
    public string Id { get; set; } = "";

    /// <summary>状态：idle / submitted / running / done / error。</summary>
    public string State { get; set; } = "idle";

    /// <summary>人话说明。</summary>
    public string Message { get; set; } = "";

    /// <summary>输入文件名（面板回显）。</summary>
    public string InputPreview { get; set; } = "";

    /// <summary>用的音色模型。</summary>
    public string Model { get; set; } = "";

    /// <summary>产物文件名。</summary>
    public string File { get; set; } = "";

    /// <summary>产物音频秒数。</summary>
    public double AudioSeconds { get; set; }

    /// <summary>变声耗时（秒）。</summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>提交时刻。</summary>
    public string SubmittedAt { get; set; } = "";

    /// <summary>结束时刻。</summary>
    public string FinishedAt { get; set; } = "";

    /// <summary>本批共几条（单条任务也是 1）。</summary>
    public int Total { get; set; }

    /// <summary>本批已成功几条。</summary>
    public int Done { get; set; }

    /// <summary>本批失败几条。</summary>
    public int Failed { get; set; }

    /// <summary>逐条状态（批量变声；单条任务也有一条）。</summary>
    public List<RvcJobItem> Items { get; set; } = new List<RvcJobItem>();
}

/// <summary>
/// 批量变声里的一条（一段输入 × 一个音色）——面板逐条看成败，不看整体一句话。
/// </summary>
public sealed class RvcJobItem
{
    /// <summary>输入文件名。</summary>
    public string Input { get; set; } = "";

    /// <summary>用的音色模型文件名。</summary>
    public string Model { get; set; } = "";

    /// <summary>状态：pending / running / done / error。</summary>
    public string State { get; set; } = "pending";

    /// <summary>产物文件名（成功才有）。</summary>
    public string File { get; set; } = "";

    /// <summary>人话说明。</summary>
    public string Message { get; set; } = "";

    /// <summary>产物音频秒数。</summary>
    public double AudioSeconds { get; set; }

    /// <summary>这一条耗时（秒）。</summary>
    public double ElapsedSeconds { get; set; }
}

/// <summary>
/// 本机 RVC 环境实况——解释器 / 代码 / HuBERT / RMVPE / 音色模型 / 依赖在不在。全部来自磁盘实测。
/// </summary>
public sealed class RvcEnvSnapshot
{
    /// <summary>解释器在位。</summary>
    public bool PythonOk { get; set; }

    /// <summary>推理代码（rvc_lite）在位。</summary>
    public bool RepoOk { get; set; }

    /// <summary>HuBERT 权重在位。</summary>
    public bool HubertOk { get; set; }

    /// <summary>RMVPE 权重在位。</summary>
    public bool RmvpeOk { get; set; }

    /// <summary>音色模型个数。</summary>
    public int ModelCount { get; set; }

    /// <summary>检索索引个数。</summary>
    public int IndexCount { get; set; }

    /// <summary>依赖齐备（faiss / pyworld / parselmouth / torchcrepe / soundfile）。</summary>
    public bool DepsOk { get; set; }

    /// <summary>缺的依赖名（人话）。</summary>
    public List<string> MissingDeps { get; set; } = new List<string>();

    /// <summary>能不能变声。</summary>
    public bool CanGenerate { get; set; }

    /// <summary>缺什么（人话，逐条）。</summary>
    public List<string> Blockers { get; set; } = new List<string>();

    /// <summary>探测时刻。</summary>
    public string CheckedAt { get; set; } = "";
}

/// <summary>
/// 一个音色模型的清单条目（面板下拉用）。
/// </summary>
public sealed class RvcModelItem
{
    /// <summary>文件名（weights 目录下的 .pth）。</summary>
    public string Name { get; set; } = "";

    /// <summary>显示名（不含扩展名）。</summary>
    public string Title { get; set; } = "";

    /// <summary>文件字节数。</summary>
    public long Bytes { get; set; }

    /// <summary>配套的检索索引文件名（没有给空串）。</summary>
    public string Index { get; set; } = "";
}

/// <summary>
/// 本地生成板块（RVC 变声）的服务面——环境探针 + 配置 + 常驻引擎投递 + 产物清单。
/// <para>与 IrodoriTTS 平行：那边出人声、这边换音色；同样不装独立运行时，直接用 ComfyUI 的 python_embeded。</para>
/// </summary>
public sealed class RvcService
{
    /// <summary>变声链需要的 Python 依赖（模块名 + 面板上的包名）。</summary>
    private static readonly (string Module, string Label)[] Deps =
    {
        ("faiss", "faiss-cpu"),
        ("pyworld", "pyworld"),
        ("parselmouth", "praat-parselmouth"),
        ("torchcrepe", "torchcrepe"),
        ("soundfile", "soundfile"),
        ("librosa", "librosa"),
    };

    /// <summary>认作音频的扩展名（输入给目录时按它筛一层）。</summary>
    private static readonly string[] AudioExtensions = new string[]
    {
        ".mp3", ".wav", ".m4a", ".flac", ".aac", ".ogg", ".opus", ".wma",
    };

    /// <summary>配置落盘路径。</summary>
    private readonly string _configPath;

    /// <summary>产物落点目录。</summary>
    private readonly string _outDir;

    /// <summary>状态锁。</summary>
    private readonly object _gate = new object();

    /// <summary>常驻引擎。</summary>
    private readonly RvcEngine _engine = new RvcEngine();

    /// <summary>当前配置。</summary>
    private RvcConfig _config;

    /// <summary>当前任务状态。</summary>
    private RvcJobStatus _job = new RvcJobStatus();

    /// <summary>以选项构造。</summary>
    /// <param name="defaults">默认配置（路径来自命令行选项）。</param>
    /// <param name="dataDir">数据目录（配置落点）。</param>
    /// <param name="outDir">产物落点目录。</param>
    public RvcService(RvcConfig defaults, string dataDir, string outDir)
    {
        _configPath = Path.Combine(dataDir, "rvc-config.json");
        _outDir = outDir;
        _config = LoadConfig();

        if (_config.PythonPath.Length == 0)
        {
            _config.PythonPath = defaults.PythonPath;
        }

        if (_config.RepoDir.Length == 0)
        {
            _config.RepoDir = defaults.RepoDir;
        }

        if (_config.ModelsRoot.Length == 0)
        {
            _config.ModelsRoot = defaults.ModelsRoot;
        }

        ApplyEnginePaths();

        // 送声驱动（VB-CABLE）：包随软件走（runtime\vbcable），解压落 data\vbcable
        _vbCable = new VbCableService(
            Path.Combine(AppContext.BaseDirectory, "runtime", "vbcable"),
            Path.Combine(dataDir, "vbcable"));

        // 实时段初始化（线 3 追加——读 rvc-realtime-config.json 并把路径推给流式 worker）
        InitRealtime(defaults, dataDir);
    }

    /// <summary>产物落点目录。</summary>
    public string OutDir
    {
        get { return _outDir; }
    }

    /// <summary>常驻引擎状态（面板显示这一路的死活）。</summary>
    public RvcEngineState Engine
    {
        get { return _engine.State; }
    }

    /// <summary>当前任务状态（拷贝，防外部改）。</summary>
    /// <returns>任务状态副本。</returns>
    public RvcJobStatus Job()
    {
        lock (_gate)
        {
            return new RvcJobStatus
            {
                Id = _job.Id,
                State = _job.State,
                Message = _job.Message,
                InputPreview = _job.InputPreview,
                Model = _job.Model,
                File = _job.File,
                AudioSeconds = _job.AudioSeconds,
                ElapsedSeconds = _job.ElapsedSeconds,
                SubmittedAt = _job.SubmittedAt,
                FinishedAt = _job.FinishedAt,
                Total = _job.Total,
                Done = _job.Done,
                Failed = _job.Failed,
                Items = CopyItems(_job.Items),
            };
        }
    }
    /// <summary>逐条状态深拷贝（防外部改）。</summary>
    /// <param name="items">源清单。</param>
    /// <returns>拷贝。</returns>
    private static List<RvcJobItem> CopyItems(List<RvcJobItem> items)
    {
        var list = new List<RvcJobItem>();
        foreach (RvcJobItem item in items)
        {
            list.Add(new RvcJobItem
            {
                Input = item.Input,
                Model = item.Model,
                State = item.State,
                File = item.File,
                Message = item.Message,
                AudioSeconds = item.AudioSeconds,
                ElapsedSeconds = item.ElapsedSeconds,
            });
        }

        return list;
    }

    /// <summary>面板快照——环境 + 配置 + 任务 + 引擎 + 音色模型清单 + 产物。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>快照对象（匿名结构，直接序列化给前端）。</returns>
    public Task<object> SnapshotAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RvcEnvSnapshot env = Probe();

        object snapshot = new
        {
            env = new
            {
                pythonOk = env.PythonOk,
                repoOk = env.RepoOk,
                hubertOk = env.HubertOk,
                rmvpeOk = env.RmvpeOk,
                modelCount = env.ModelCount,
                indexCount = env.IndexCount,
                depsOk = env.DepsOk,
                missingDeps = env.MissingDeps,
                canGenerate = env.CanGenerate,
                blockers = env.Blockers,
                checkedAt = env.CheckedAt,
            },
            config = _config,
            job = Job(),
            engine = Engine,
            models = ListModels(),
            outputs = ListOutputs(),
            outDir = _outDir,
        };

        return Task.FromResult(snapshot);
    }

    /// <summary>改配置（传进来的非空字段覆盖，其余不动）。</summary>
    /// <param name="next">新配置。</param>
    /// <returns>落盘后的配置。</returns>
    public RvcConfig UpdateConfig(RvcConfig next)
    {
        lock (_gate)
        {
            _config.Model = next.Model ?? "";
            _config.InputPath = next.InputPath ?? "";
            _config.Sid = Clamp(next.Sid, 0, 200);
            _config.F0UpKey = Clamp(next.F0UpKey, -24, 24);
            _config.F0Method = NormalizeF0(next.F0Method);
            _config.IndexRate = Clamp(next.IndexRate, 0, 1);
            _config.FilterRadius = Clamp(next.FilterRadius, 0, 7);
            _config.ResampleSr = next.ResampleSr <= 0 ? 0 : Clamp(next.ResampleSr, 16000, 48000);
            _config.RmsMixRate = Clamp(next.RmsMixRate, 0, 1);
            _config.Protect = Clamp(next.Protect, 0, 0.5);
            _config.Device = string.IsNullOrEmpty(next.Device) ? "cuda" : next.Device;
            _config.Half = next.Half;
            _config.Models = next.Models ?? new List<string>();

            if (next.PythonPath.Length > 0)
            {
                _config.PythonPath = next.PythonPath;
            }

            if (next.RepoDir.Length > 0)
            {
                _config.RepoDir = next.RepoDir;
            }

            if (next.ModelsRoot.Length > 0)
            {
                _config.ModelsRoot = next.ModelsRoot;
            }

            SaveConfig();
        }

        // 路径 / 设备 / 精度可能变了——把在跑的引擎按新参数重启（Prepare 内部会判断是否真的变了）
        ApplyEnginePaths();
        return _config;
    }

    /// <summary>按当前配置投递一次变声（立刻返回，跑在后台，面板轮询状态）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否受理, 说明)。</returns>
    public Task<(bool Ok, string Message)> GenerateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        RvcEnvSnapshot env = Probe();
        if (!env.CanGenerate)
        {
            string reason = env.Blockers.Count > 0 ? env.Blockers[0] : "前置条件不足";
            return Task.FromResult((false, reason));
        }

        RvcConfig snapshot;
        List<string> inputs;
        List<string> models;
        RvcJobStatus batch;
        lock (_gate)
        {
            if (_job.State == "submitted" || _job.State == "running")
            {
                return Task.FromResult((false, "上一批还在跑——先等它出来，或点「停止引擎」"));
            }

            string input = (_config.InputPath ?? "").Trim();
            inputs = ExpandInputs(input);
            if (inputs.Count == 0)
            {
                string shown = input;
                if (shown.Length == 0)
                {
                    shown = "未填";
                }

                return Task.FromResult((false, "输入音频不在位：" + shown));
            }

            models = ExpandModels(_config);
            if (models.Count == 0)
            {
                return Task.FromResult((false, "没选音色模型"));
            }

            snapshot = new RvcConfig
            {
                Model = models[0],
                Models = models,
                InputPath = input,
                Sid = _config.Sid,
                F0UpKey = _config.F0UpKey,
                F0Method = _config.F0Method,
                IndexRate = _config.IndexRate,
                FilterRadius = _config.FilterRadius,
                ResampleSr = _config.ResampleSr,
                RmsMixRate = _config.RmsMixRate,
                Protect = _config.Protect,
                Device = _config.Device,
                Half = _config.Half,
                PythonPath = _config.PythonPath,
                RepoDir = _config.RepoDir,
                ModelsRoot = _config.ModelsRoot,
            };

            string preview;
            if (inputs.Count == 1)
            {
                preview = Path.GetFileName(inputs[0]);
            }
            else
            {
                preview = Path.GetFileName(inputs[0]) + " 等 " + inputs.Count.ToString() + " 个文件";
            }

            string modelLabel;
            if (models.Count == 1)
            {
                modelLabel = models[0];
            }
            else
            {
                modelLabel = models.Count.ToString() + " 个音色";
            }

            batch = new RvcJobStatus
            {
                Id = DateTime.Now.ToString("HHmmss"),
                State = "submitted",
                Message = "已提交 " + (inputs.Count * models.Count).ToString() + " 条，等引擎接手…",
                InputPreview = preview,
                Model = modelLabel,
                SubmittedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };

            foreach (string file in inputs)
            {
                foreach (string model in models)
                {
                    batch.Items.Add(new RvcJobItem
                    {
                        Input = Path.GetFileName(file),
                        Model = model,
                        State = "pending",
                        Message = "排队中",
                    });
                }
            }

            batch.Total = batch.Items.Count;
            _job = batch;
        }

        _ = Task.Run(() => RunJobAsync(snapshot, batch, inputs, models));
        return Task.FromResult((true, "已提交 " + batch.Total.ToString() + " 条（输入 " + inputs.Count.ToString()
            + " 个 × 音色 " + models.Count.ToString() + " 个 · F0 " + snapshot.F0Method
            + " · 变调 " + snapshot.F0UpKey.ToString("0.#") + "）"));
    }

    /// <summary>停掉常驻引擎（把显存还回去；在跑的这一段作废）。</summary>
    /// <returns>说明。</returns>
    public string Stop()
    {
        _engine.Stop();
        lock (_gate)
        {
            if (_job.State == "submitted" || _job.State == "running")
            {
                _job.State = "idle";
                _job.Message = "已停引擎（这一批作废）";
                _job.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                foreach (RvcJobItem item in _job.Items)
                {
                    if (item.State == "pending" || item.State == "running")
                    {
                        item.State = "error";
                        item.Message = "已停引擎";
                    }
                }
            }
        }

        return "已停引擎";
    }

    /// <summary>weights 目录里的音色模型清单。</summary>
    /// <returns>模型条目列表。</returns>
    public List<RvcModelItem> ListModels()
    {
        var list = new List<RvcModelItem>();
        string dir = Path.Combine(_config.ModelsRoot ?? "", "weights");
        if (dir.Length == 0 || !Directory.Exists(dir))
        {
            return list;
        }

        try
        {
            foreach (FileInfo file in new DirectoryInfo(dir).GetFiles("*.pth"))
            {
                string stem = Path.GetFileNameWithoutExtension(file.Name);
                list.Add(new RvcModelItem
                {
                    Name = file.Name,
                    Title = stem,
                    Bytes = file.Length,
                    Index = FindIndexName(stem),
                });
            }
        }
        catch (IOException)
        {
            return list;
        }

        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
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

    /// <summary>模型名对应的检索索引文件名（没有返回空串）。</summary>
    /// <param name="stem">模型名（不含扩展名）。</param>
    /// <returns>索引文件名或空串。</returns>
    private string FindIndexName(string stem)
    {
        string dir = Path.Combine(_config.ModelsRoot ?? "", "indices");
        if (!Directory.Exists(dir))
        {
            return "";
        }

        string key = stem.ToLowerInvariant();
        try
        {
            foreach (string path in Directory.GetFiles(dir, "*.index"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                string other = name.ToLowerInvariant();
                if (other.Contains(key) || key.Contains(other))
                {
                    return Path.GetFileName(path);
                }
            }
        }
        catch (IOException)
        {
            return "";
        }

        return "";
    }

    /// <summary>环境探针（只看磁盘，不启动进程）。</summary>
    /// <returns>环境快照。</returns>
    private RvcEnvSnapshot Probe()
    {
        RvcConfig cfg;
        lock (_gate)
        {
            cfg = _config;
        }

        var blockers = new List<string>();
        string python = cfg.PythonPath;
        bool pythonOk = python.Length > 0 && File.Exists(python);
        if (!pythonOk)
        {
            blockers.Add("解释器不在位：" + (python.Length > 0 ? python : "未配置"));
        }

        string sitePackages = "";
        if (python.Length > 0)
        {
            string? embedRoot = Path.GetDirectoryName(python);
            if (embedRoot is not null)
            {
                sitePackages = Path.Combine(embedRoot, "Lib", "site-packages");
            }
        }

        bool repoOk = cfg.RepoDir.Length > 0 && File.Exists(Path.Combine(cfg.RepoDir, "rvc_lite", "hubert.py"));
        if (!repoOk)
        {
            blockers.Add("推理代码不完整（缺 rvc_lite\\hubert.py）：" + cfg.RepoDir);
        }

        string root = cfg.ModelsRoot ?? "";
        string hubert = Path.Combine(root, "hubert", "hubert_base.pt");
        bool hubertOk = root.Length > 0 && File.Exists(hubert);
        if (!hubertOk)
        {
            blockers.Add("HuBERT 权重不在位：" + hubert);
        }

        string rmvpe = Path.Combine(root, "rmvpe", "rmvpe.pt");
        bool rmvpeOk = root.Length > 0 && File.Exists(rmvpe);
        if (!rmvpeOk)
        {
            blockers.Add("RMVPE 权重不在位：" + rmvpe);
        }

        int modelCount = CountFiles(Path.Combine(root, "weights"), "*.pth");
        if (modelCount == 0)
        {
            blockers.Add("音色模型目录里没有 .pth：" + Path.Combine(root, "weights"));
        }

        int indexCount = CountFiles(Path.Combine(root, "indices"), "*.index");

        var missing = new List<string>();
        foreach ((string module, string label) in Deps)
        {
            if (!ModulePresent(sitePackages, module))
            {
                missing.Add(label);
            }
        }

        bool depsOk = missing.Count == 0;
        if (!depsOk)
        {
            blockers.Add("Python 依赖缺：" + string.Join(" / ", missing) + "（装进 " + sitePackages + "）");
        }

        return new RvcEnvSnapshot
        {
            PythonOk = pythonOk,
            RepoOk = repoOk,
            HubertOk = hubertOk,
            RmvpeOk = rmvpeOk,
            ModelCount = modelCount,
            IndexCount = indexCount,
            DepsOk = depsOk,
            MissingDeps = missing,
            CanGenerate = blockers.Count == 0,
            Blockers = blockers,
            CheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };
    }

    /// <summary>把配置里的路径推给常驻引擎（参数没变就什么都不做）。</summary>
    private void ApplyEnginePaths()
    {
        _engine.Prepare(_config.PythonPath, _config.RepoDir, _config.ModelsRoot, _config.Device, _config.Half);
    }

    /// <summary>后台跑一批变声（逐条投递），结束后更新批次状态。</summary>
    /// <param name="cfg">本次参数快照。</param>
    /// <param name="batch">本批任务（面板读的就是它）。</param>
    /// <param name="inputs">输入音频清单。</param>
    /// <param name="models">音色清单。</param>
    /// <returns>无。</returns>
    private async Task RunJobAsync(RvcConfig cfg, RvcJobStatus batch, List<string> inputs, List<string> models)
    {
        lock (_gate)
        {
            batch.State = "running";
            batch.Message = "变声中…（0/" + batch.Items.Count.ToString() + "）";
        }

        try
        {
            Directory.CreateDirectory(_outDir);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            lock (_gate)
            {
                batch.State = "error";
                batch.Message = "产物目录不可写：" + ex.Message;
                batch.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }

            return;
        }

        int index = 0;
        foreach (string input in inputs)
        {
            foreach (string model in models)
            {
                index = index + 1;
                lock (_gate)
                {
                    batch.Items[index - 1].State = "running";
                    batch.Items[index - 1].Message = "变声中…";
                    batch.Message = "变声中…（" + index.ToString() + "/" + batch.Items.Count.ToString() + "）";
                }

                DateTime started = DateTime.Now;
                string outPath = BuildOutPath(input, model);
                (bool ok, string file, double audioSeconds, string message) = await _engine.RunAsync(
                    input,
                    model,
                    cfg.Sid,
                    cfg.F0UpKey,
                    cfg.F0Method,
                    cfg.IndexRate,
                    cfg.FilterRadius,
                    cfg.ResampleSr,
                    cfg.RmsMixRate,
                    cfg.Protect,
                    outPath,
                    CancellationToken.None).ConfigureAwait(false);

                double elapsed = Math.Round((DateTime.Now - started).TotalSeconds, 2);
                lock (_gate)
                {
                    RvcJobItem item = batch.Items[index - 1];
                    item.Message = message;
                    item.AudioSeconds = audioSeconds;
                    item.ElapsedSeconds = elapsed;
                    batch.ElapsedSeconds = elapsed;
                    if (ok)
                    {
                        item.State = "done";
                        item.File = file;
                        batch.Done = batch.Done + 1;
                        batch.File = file;
                        batch.AudioSeconds = audioSeconds;
                    }
                    else
                    {
                        item.State = "error";
                        item.File = "";
                        batch.Failed = batch.Failed + 1;
                    }
                }
            }
        }

        lock (_gate)
        {
            if (batch.Failed > 0)
            {
                batch.State = "error";
                batch.Message = "共 " + batch.Total.ToString() + " 条：成功 " + batch.Done.ToString()
                    + " · 失败 " + batch.Failed.ToString();
            }
            else
            {
                batch.State = "done";
                batch.Message = "全部完成（" + batch.Done.ToString() + " 条）";
            }

            batch.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }

    /// <summary>模块是否在位——包目录 / 单文件 .py / 扩展 .pyd 三种形态都认。</summary>
    /// <param name="sitePackages">site-packages 目录。</param>
    /// <param name="module">模块名。</param>
    /// <returns>在位返回 true。</returns>
    private static bool ModulePresent(string sitePackages, string module)
    {
        if (sitePackages.Length == 0 || module.Length == 0 || !Directory.Exists(sitePackages))
        {
            return false;
        }

        if (Directory.Exists(Path.Combine(sitePackages, module)))
        {
            return true;
        }

        if (File.Exists(Path.Combine(sitePackages, module + ".py")))
        {
            return true;
        }

        try
        {
            return Directory.GetFiles(sitePackages, module + "*.pyd").Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>数目录里某类文件的个数（目录不存在给 0）。</summary>
    /// <param name="dir">目录。</param>
    /// <param name="pattern">模式。</param>
    /// <returns>个数。</returns>
    private static int CountFiles(string dir, string pattern)
    {
        if (dir.Length == 0 || !Directory.Exists(dir))
        {
            return 0;
        }

        try
        {
            return Directory.GetFiles(dir, pattern).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>F0 取法收敛到四档。</summary>
    /// <param name="value">原值。</param>
    /// <returns>合法值。</returns>
    private static string NormalizeF0(string value)
    {
        string mode = (value ?? "").Trim().ToLowerInvariant();
        return mode == "pm" || mode == "harvest" || mode == "crepe" ? mode : "rmvpe";
    }

    /// <summary>夹到区间内。</summary>
    /// <param name="value">原值。</param>
    /// <param name="min">下限。</param>
    /// <param name="max">上限。</param>
    /// <returns>夹好的值。</returns>
    private static int Clamp(int value, int min, int max)
    {
        return value < min ? min : (value > max ? max : value);
    }

    /// <summary>夹到区间内。</summary>
    /// <param name="value">原值。</param>
    /// <param name="min">下限。</param>
    /// <param name="max">上限。</param>
    /// <returns>夹好的值。</returns>
    private static double Clamp(double value, double min, double max)
    {
        if (double.IsNaN(value))
        {
            return min;
        }

        return value < min ? min : (value > max ? max : value);
    }

    /// <summary>读配置（没有或坏了给默认值）。</summary>
    /// <returns>配置。</returns>
    private RvcConfig LoadConfig()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                return new RvcConfig();
            }

            string json = File.ReadAllText(_configPath);
            RvcConfig? loaded = JsonSerializer.Deserialize<RvcConfig>(json, JsonOptions());
            return loaded ?? new RvcConfig();
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
        {
            return new RvcConfig();
        }
    }

    /// <summary>写配置。</summary>
    private void SaveConfig()
    {
        try
        {
            string json = JsonSerializer.Serialize(_config, JsonOptions());
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // 落盘失败不静默——面板的配置读写错误由环境探针之外的路径暴露
            Console.Error.WriteLine("写 rvc-config.json 失败：" + ex.Message);
        }
    }

    /// <summary>序列化选项（缩进 + 不转义中文）。</summary>
    /// <returns>选项。</returns>
    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
    }
    /// <summary>展开输入——给文件就一条，给目录取一层音频（按扩展名筛）。</summary>
    /// <param name="path">输入文件或目录完整路径。</param>
    /// <returns>输入音频完整路径清单（可能为空）。</returns>
    private static List<string> ExpandInputs(string path)
    {
        var list = new List<string>();
        if (File.Exists(path))
        {
            list.Add(path);
            return list;
        }

        if (!Directory.Exists(path))
        {
            return list;
        }

        try
        {
            foreach (string file in Directory.GetFiles(path))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (Array.IndexOf(AudioExtensions, ext) >= 0)
                {
                    list.Add(file);
                }
            }
        }
        catch (IOException)
        {
            return list;
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }
    /// <summary>展开音色——多选清单优先，空则用单值那个。</summary>
    /// <param name="cfg">配置。</param>
    /// <returns>音色模型文件名清单（可能为空）。</returns>
    private static List<string> ExpandModels(RvcConfig cfg)
    {
        var list = new List<string>();
        if (cfg.Models != null)
        {
            foreach (string name in cfg.Models)
            {
                if (!string.IsNullOrWhiteSpace(name) && list.IndexOf(name) < 0)
                {
                    list.Add(name);
                }
            }
        }

        if (list.Count == 0 && cfg.Model.Length > 0)
        {
            list.Add(cfg.Model);
        }

        return list;
    }
    /// <summary>产物落点——时间戳_输入名_音色名.wav，同秒撞名时补序号。</summary>
    /// <param name="input">输入音频完整路径。</param>
    /// <param name="model">音色模型文件名。</param>
    /// <returns>产物完整路径。</returns>
    private string BuildOutPath(string input, string model)
    {
        string stem = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_"
            + Path.GetFileNameWithoutExtension(input) + "_"
            + Path.GetFileNameWithoutExtension(model);
        string path = Path.Combine(_outDir, stem + ".wav");
        int n = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(_outDir, stem + "_" + n.ToString() + ".wav");
            n = n + 1;
        }

        return path;
    }

    // ===== 实时段（v0.38 · 线 3）——以上离线段一律不动，本段只追加 =====

    /// <summary>实时段状态锁（与离线段分开——实时侧起停 worker 会阻塞数秒）。</summary>
    private readonly object _rtGate = new object();

    /// <summary>实时配置落盘路径。</summary>
    private string _rtConfigPath = "";

    /// <summary>实时引擎（流式 worker；与离线常驻引擎分立的第二套进程）。</summary>
    private readonly RvcRealtimeEngine _rtEngine = new RvcRealtimeEngine();

    /// <summary>当前实时配置。</summary>
    private RvcRealtimeConfig _rtConfig = new RvcRealtimeConfig();

    /// <summary>worker 脚本源码（线 2 的 RvcRealtimeWorkerScript 经此注入；空 = 只认盘上脚本）。</summary>
    private string _rtWorkerSource = "";

    /// <summary>桥接要用的 VITS 运行面来源（Program 里接到 VitsService——路径 / 名册 / F0 查询）。</summary>
    private Func<RvcVitsRuntime?>? _vitsRuntimeSource;

    /// <summary>VB-CABLE 端点辅助（检测驱动 / 解内置包 / 提权安装——送声给其他软件用的虚拟声卡）。</summary>
    private readonly VbCableService _vbCable;

    /// <summary>最近一次设备枚举结果（开流前校验配置里的端点还在不在——卸载虚拟声卡后索引会位移）。</summary>
    private List<RvcHostApiItem> _lastHostApis = new List<RvcHostApiItem>();

    /// <summary>实时链状态（面板显示这一路的死活与延迟）。</summary>
    public RvcRealtimeState Realtime
    {
        get { return _rtEngine.State; }
    }

    /// <summary>离线链是否占着显卡（离线引擎在跑，或任务在途）——实时开流前查它。</summary>
    public bool OfflineBusy
    {
        get
        {
            if (_engine.Alive)
            {
                return true;
            }

            lock (_gate)
            {
                bool running = _job.State == "submitted" || _job.State == "running";
                return running;
            }
        }
    }

    /// <summary>注入 worker 脚本源码（线 2 落地后由 Program 调用一次；空 = 不注入）。</summary>
    /// <param name="source">Python 源码。</param>
    public void SetRealtimeWorkerSource(string source)
    {
        if (source == null)
        {
            _rtWorkerSource = "";
            return;
        }

        _rtWorkerSource = source;
    }

    /// <summary>当前实时配置（拷贝，防外部改）。</summary>
    /// <returns>配置副本。</returns>
    public RvcRealtimeConfig RealtimeConfig()
    {
        lock (_rtGate)
        {
            return CloneRealtimeConfig(_rtConfig);
        }
    }
    /// <summary>接上桥接要用的 VITS 运行面来源（Program 里指向 VitsService——只出路径与名册，不出设置）。</summary>
    /// <param name="source">返回运行面的回调（null = 桥接不可用）。</param>
    public void SetVitsRuntimeSource(Func<RvcVitsRuntime?> source)
    {
        _vitsRuntimeSource = source;
    }

    /// <summary>当前桥接设置——模型 / 目标说话人 / 自动档取自 RVC 自己的配置，路径与 F0 名册取自 VITS 运行面。</summary>
    /// <returns>设置快照（VITS 运行面没接上时返回空设置）。</returns>
    public RvcVitsBridgeSpec BridgeSpec()
    {
        RvcRealtimeConfig cfg;
        lock (_rtGate)
        {
            cfg = CloneRealtimeConfig(_rtConfig);
        }

        var spec = new RvcVitsBridgeSpec
        {
            Model = cfg.VcBridgeModel,
            Src = 0,
            Dst = cfg.VcBridgeDst,
            SrcAuto = cfg.VcBridgeAutoSrc,
            F0Auto = cfg.VcBridgeAutoF0,
        };

        Func<RvcVitsRuntime?>? source = _vitsRuntimeSource;
        RvcVitsRuntime? rt = source?.Invoke();
        if (rt == null)
        {
            return spec;
        }

        spec.ModelsRoot = rt.ModelsRoot;
        spec.Repo = rt.Repo;
        spec.Device = rt.Device;
        spec.F0Profile = rt.F0Profile;
        spec.DstF0 = rt.F0Of?.Invoke(spec.Model, spec.Dst) ?? 0;
        return spec;
    }
    /// <summary>VITS 设置变了——桥接开着且流在跑时把新设置推给 worker（换模型由 worker 下一段重载）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 人话)。</returns>
    public async Task<(bool Ok, string Message)> RealtimeRefreshBridgeAsync(CancellationToken ct)
    {
        RvcRealtimeConfig cfg = RealtimeConfig();
        if (!cfg.VcBridge)
        {
            return (true, "桥接没开——设置已存，下次打开生效");
        }

        RvcRealtimeState state = _rtEngine.State;
        if (state.State != "running" && state.State != "starting")
        {
            return (true, "实时流没在跑——设置已存，下次开流生效");
        }

        _rtEngine.BridgeSpec = BridgeSpec();
        (bool ok, string message, List<string> applied) = await _rtEngine
            .UpdateAsync(cfg, ct).ConfigureAwait(false);
        return (ok, message);
    }

    /// <summary>实时面板快照——环境 + 配置 + 状态 + 设备区间 + 送声驱动（VB-CABLE）+ 互斥状态。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>快照对象（匿名结构，直接序列化给前端）。</returns>
    public Task<object> RealtimeSnapshotAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        RvcRealtimeEnvSnapshot env = ProbeRealtime();
        VbCableStatus cable = _vbCable.Detect();
        RvcRealtimeConfig cfg = RealtimeConfig();
        RvcRealtimeState state = _rtEngine.State;
        bool busy = OfflineBusy;

        object snapshot = new
        {
            env = new
            {
                pythonOk = env.PythonOk,
                repoOk = env.RepoOk,
                scriptOk = env.ScriptOk,
                soundDeviceOk = env.SoundDeviceOk,
                torchGateOk = env.TorchGateOk,
                modelCount = env.ModelCount,
                canStart = env.CanStart,
                blockers = env.Blockers,
                checkedAt = env.CheckedAt,
            },
            config = cfg,
            state = state,
            vbcable = cable,
            offlineBusy = busy,
            models = ListModels(),
            scriptPath = _rtEngine.ScriptPath,
            bridge = BridgeSpec(),
            hotFields = RvcRealtimeProtocol.HotFields,
        };

        return Task.FromResult(snapshot);
    }

    /// <summary>
    /// 开流（互斥守卫在前：离线引擎占着卡就明确阻塞，不静默抢卡）。
    /// </summary>
    /// <param name="next">新配置（null = 用当前配置）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 人话)。</returns>
    public async Task<(bool Ok, string Message)> RealtimeStartAsync(
        RvcRealtimeConfig next,
        CancellationToken ct)
    {
        if (next != null)
        {
            SaveRealtimeConfig(next);
        }

        if (OfflineBusy)
        {
            return (false, "离线引擎正占着显卡——先停「离线」子签的引擎，再开实时（不自动杀进程）");
        }

        RvcRealtimeEnvSnapshot env = ProbeRealtime();
        if (!env.CanStart)
        {
            if (env.Blockers.Count > 0)
            {
                return (false, env.Blockers[0]);
            }

            return (false, "前置条件不足");
        }

        RvcRealtimeConfig cfg = RealtimeConfig();
        if (cfg.Model.Length == 0)
        {
            return (false, "没选音色模型");
        }

        // 还没枚举过设备：先枚举一次再校验（worker 起好后开流复用同一个进程，成本很小）——
        // 卸载 / 重装声卡驱动后索引会整体位移，不校验会把声音送到意外的端点上
        if (_lastHostApis.Count == 0)
        {
            (bool devOk, List<RvcHostApiItem> apis, string _) = await RealtimeDevicesAsync(ct).ConfigureAwait(false);
            if (devOk)
            {
                _lastHostApis = apis;
            }
        }

        string? missing = MissingEndpoint(cfg);
        if (missing != null)
        {
            return (false, missing);
        }

        ApplyRealtimeEnginePaths();
        RvcVitsBridgeSpec spec = BridgeSpec();
        if (cfg.VcBridge && spec.Model.Length == 0)
        {
            return (false, "桥接 VITS-VC 已打开，但还没选 VITS 模型——在「桥接 VITS 语音转换」块里选一个模型再开流");
        }

        _rtEngine.BridgeSpec = spec;
        return await _rtEngine.StartAsync(cfg, _rtWorkerSource, ct).ConfigureAwait(false);
    }

    /// <summary>配置里的端点还在不在（按名字比，不按索引——卸载 / 重装声卡驱动后索引会整体位移）。</summary>
    /// <param name="cfg">实时配置。</param>
    /// <returns>不在位的人话（都在位或还没枚举过返回 null）。</returns>
    private string? MissingEndpoint(RvcRealtimeConfig cfg)
    {
        List<RvcHostApiItem> apis = _lastHostApis;
        if (apis.Count == 0)
        {
            return null;
        }

        string? input = MissingOne(apis, cfg.Input, true);
        if (input != null)
        {
            return input;
        }

        return MissingOne(apis, cfg.Output, false);
    }

    /// <summary>单个端点的在位判定。</summary>
    /// <param name="apis">设备枚举结果。</param>
    /// <param name="device">配置里的端点。</param>
    /// <param name="wantInput">true = 按输入端判，false = 按输出端判。</param>
    /// <returns>不在位的人话（在位返回 null）。</returns>
    private static string? MissingOne(List<RvcHostApiItem> apis, RvcRealtimeDevice device, bool wantInput)
    {
        if (device == null)
        {
            return null;
        }

        string name = device.Name ?? "";
        if (name.Length == 0 || device.Index < 0)
        {
            return null;
        }

        foreach (RvcHostApiItem api in apis)
        {
            foreach (RvcDeviceItem item in api.Devices)
            {
                if (item.Name == name && (wantInput ? item.In : item.Out))
                {
                    return null;
                }
            }
        }

        return (wantInput ? "输入设备「" : "输出设备「") + name
            + "」已不在位（可能卸载了旧虚拟声卡）——点「刷新设备」重选一个再开流";
    }

    /// <summary>停流（worker 保留，下次开流不用重载模型）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 人话)。</returns>
    public async Task<(bool Ok, string Message)> RealtimeStopAsync(CancellationToken ct)
    {
        return await _rtEngine.StopStreamAsync(ct).ConfigureAwait(false);
    }

    /// <summary>改实时配置：落盘后按字段分流——热更字段直接生效；其余字段在流已开时自动按新配置重开流（换音色 / 设备 / 采样率 / F0 取法 / 精度不必手动操作）。</summary>
    /// <param name="next">新配置。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 人话, 生效字段, 是否需重开流)。</returns>
    public async Task<(bool Ok, string Message, List<string> Applied, bool NeedRestart)> RealtimeUpdateAsync(
        RvcRealtimeConfig next,
        CancellationToken ct)
    {
        var none = new List<string>();
        if (next == null)
        {
            return (false, "参数不完整", none, false);
        }

        RvcRealtimeConfig before = RealtimeConfig();
        RvcRealtimeState stateBefore = _rtEngine.State;
        RvcRealtimeConfig saved = SaveRealtimeConfig(next);
        bool needRestart = RealtimeNeedsRestart(before, saved);

        // 判活用改前状态——SaveRealtimeConfig 里解释器 / 代码目录 / 模型根 / 设备 / 精度
        // 任一项变了会先把 worker 停掉；在这里取状态会被误判成「流没在跑」，
        // 表现为改推理设备 / 半精度后声音直接没了、也没有重开（用户不会想到是参数没生效）
        bool live = stateBefore.State == "running" || stateBefore.State == "starting";
        if (!live)
        {
            return (true, "配置已保存（实时流没在跑，下次开流生效）", none, false);
        }

        if (needRestart)
        {
            (bool restarted, string restartMessage) = await RealtimeStartAsync(saved, ct).ConfigureAwait(false);
            if (!restarted)
            {
                return (false, "配置已保存，但按新参数重开流失败：" + restartMessage, none, true);
            }

            return (true, "已按新参数自动重开流——" + restartMessage, none, true);
        }

        _rtEngine.BridgeSpec = BridgeSpec();
        RvcVitsBridgeSpec bridgeNow = _rtEngine.BridgeSpec;
        if (saved.VcBridge && bridgeNow != null && bridgeNow.Model.Length == 0)
        {
            return (false, "桥接 VITS-VC 已保存，但还没选 VITS 模型——在「桥接 VITS 语音转换」块里选一个模型", none, false);
        }

        (bool ok, string message, List<string> applied) = await _rtEngine
            .UpdateAsync(saved, ct).ConfigureAwait(false);
        return (ok, message, applied, false);
    }

    /// <summary>桥接下拉的数据面——VITS 模型清单 + 指定模型的说话人（含自然 F0）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>快照对象。</returns>
    public Task<object> BridgeOptionsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RvcRealtimeConfig cfg = RealtimeConfig();
        Func<RvcVitsRuntime?>? source = _vitsRuntimeSource;
        RvcVitsRuntime? rt = source?.Invoke();

        var models = new List<RvcVitsModelItem>();
        var speakers = new List<RvcVitsSpeakerItem>();
        bool runtimeOk = rt != null
            && rt.ModelsRoot.Length > 0
            && Directory.Exists(rt.ModelsRoot);

        if (runtimeOk && rt != null)
        {
            string root = rt.ModelsRoot;
            try
            {
                foreach (string groupDir in Directory.GetDirectories(root))
                {
                    string group = Path.GetFileName(groupDir);
                    foreach (string pth in Directory.GetFiles(groupDir, "*.pth"))
                    {
                        string name = Path.GetFileNameWithoutExtension(pth);
                        string cfgPath = Path.Combine(groupDir, name + ".json");
                        models.Add(new RvcVitsModelItem
                        {
                            Name = name,
                            Group = group,
                            Ok = File.Exists(cfgPath),
                        });
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                runtimeOk = false;
            }
        }

        if (runtimeOk)
        {
            foreach (RvcVitsModelItem m in models)
            {
                string cfgPath = Path.Combine(rt!.ModelsRoot, m.Group, m.Name + ".json");
                (int speakers2, int rate, bool ok) = ReadVitsModelMeta(cfgPath);
                m.Speakers = speakers2;
                m.SamplingRate = rate;
                m.Ok = m.Ok && ok;
            }

            models.Sort((x, y) => string.CompareOrdinal(x.Name, y.Name));
        }

        if (rt != null && cfg.VcBridgeModel.Length > 0)
        {
            RvcVitsModelItem? hit = models.FirstOrDefault(m => m.Name == cfg.VcBridgeModel);
            if (hit != null && hit.Ok && hit.Speakers > 0)
            {
                for (int i = 0; i < hit.Speakers; i++)
                {
                    speakers.Add(new RvcVitsSpeakerItem
                    {
                        Index = i,
                        Name = "spk" + i,
                        F0 = rt.F0Of?.Invoke(hit.Name, i) ?? 0,
                    });
                }

                // 名册里的名字比 n_speakers 少是常态——有名字的覆盖上去
                foreach (string[] pair in VitsSpeakerNames(rt.ModelsRoot, cfg.VcBridgeModel))
                {
                    if (int.TryParse(pair[0], out int sid) && sid >= 0 && sid < speakers.Count)
                    {
                        speakers[sid].Name = pair[1];
                    }
                }
            }
        }

        object snapshot = new
        {
            ok = true,
            runtimeOk,
            modelsRoot = rt?.ModelsRoot ?? "",
            f0Profile = rt?.F0Profile ?? "",
            f0 = new
            {
                ready = speakers.Count(s => s.F0 > 0),
                total = speakers.Count,
            },
            models,
            speakers,
            selected = new
            {
                model = cfg.VcBridgeModel,
                dst = cfg.VcBridgeDst,
                autoSrc = cfg.VcBridgeAutoSrc,
                autoF0 = cfg.VcBridgeAutoF0,
                blocks = cfg.VcBridgeBlocks,
                enabled = cfg.VcBridge,
            },
        };

        return Task.FromResult(snapshot);
    }

    /// <summary>读 VITS 模型 config 的说话人数与采样率（读不出给 0 / false）。</summary>
    /// <param name="configPath">config 路径。</param>
    /// <returns>(说话人数, 采样率, 是否解析成功)。</returns>
    private static (int Speakers, int Rate, bool Ok) ReadVitsModelMeta(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return (0, 0, false);
        }

        try
        {
            string text = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            int speakers = 0;
            int rate = 0;
            if (root.TryGetProperty("data", out JsonElement data))
            {
                if (data.TryGetProperty("n_speakers", out JsonElement ns) && ns.ValueKind == JsonValueKind.Number)
                {
                    speakers = ns.GetInt32();
                }

                if (data.TryGetProperty("sampling_rate", out JsonElement sr) && sr.ValueKind == JsonValueKind.Number)
                {
                    rate = sr.GetInt32();
                }
            }

            return (speakers, rate, speakers > 0);
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
        {
            return (0, 0, false);
        }
    }

    /// <summary>读模型名册里「有名字」的那些说话人（索引 → 名字）。</summary>
    /// <param name="modelsRoot">模型根。</param>
    /// <param name="model">模型名。</param>
    /// <returns>索引与名字的字符串对（解析失败给空表）。</returns>
    private static List<string[]> VitsSpeakerNames(string modelsRoot, string model)
    {
        var list = new List<string[]>();
        try
        {
            foreach (string groupDir in Directory.GetDirectories(modelsRoot))
            {
                string cfgPath = Path.Combine(groupDir, model + ".json");
                if (!File.Exists(cfgPath))
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
                if (!doc.RootElement.TryGetProperty("speakers", out JsonElement arr)
                    || arr.ValueKind != JsonValueKind.Array)
                {
                    return list;
                }

                int i = 0;
                foreach (JsonElement item in arr.EnumerateArray())
                {
                    string name = item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
                    if (name.Length > 0)
                    {
                        list.Add(new[] { i.ToString(), name });
                    }

                    i++;
                }

                return list;
            }
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
        {
            return list;
        }

        return list;
    }

    /// <summary>枚举音频设备（设备类型 / 输入 / 输出下拉用；不加载模型、不开流）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, hostapi 清单, 人话)。</returns>
    public async Task<(bool Ok, List<RvcHostApiItem> HostApis, string Message)> RealtimeDevicesAsync(
        CancellationToken ct)
    {
        RvcRealtimeConfig cfg = RealtimeConfig();
        if (cfg.PythonPath.Length == 0 || !File.Exists(cfg.PythonPath))
        {
            return (false, new List<RvcHostApiItem>(), "解释器不在位：" + cfg.PythonPath);
        }

        ApplyRealtimeEnginePaths();
        (bool ok, List<RvcHostApiItem> apis, string message) = await _rtEngine
            .DevicesAsync(_rtWorkerSource, ct).ConfigureAwait(false);
        if (ok)
        {
            _lastHostApis = apis;
        }

        return (ok, apis, message);
    }

    /// <summary>自检——枚举设备后跑一段正弦进链，回延迟与每块耗时。</summary>
    /// <param name="seconds">正弦秒数（默认 3）。</param>
    /// <param name="outIndex">输出设备索引（hostapi 内编号）。</param>
    /// <param name="sampleRate">设备采样率。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 状态快照, 人话)。</returns>
    public async Task<(bool Ok, RvcRealtimeState Result, string Message)> RealtimeSelftestAsync(
        int seconds,
        int outIndex,
        int sampleRate,
        CancellationToken ct)
    {
        int length = 3;
        if (seconds > 0)
        {
            length = seconds;
        }

        int sr = 48000;
        if (sampleRate > 0)
        {
            sr = sampleRate;
        }

        ApplyRealtimeEnginePaths();
        RvcRealtimeConfig cfg = RealtimeConfig();
        return await _rtEngine.SelftestAsync(length, outIndex, sr, cfg, _rtWorkerSource, ct)
            .ConfigureAwait(false);
    }

    /// <summary>关掉实时 worker 进程（自检 / 面板退出前还资源用；下一次开流会重起）。</summary>
    public void RealtimeShutdown()
    {
        _rtEngine.Stop();
    }

    /// <summary>VB-CABLE 检测（驱动装没装 / 内置包在不在位 / 解压出来的安装器在不在）。</summary>
    /// <returns>检测结果。</returns>
    public VbCableStatus VbCableDetect()
    {
        return _vbCable.Detect();
    }

    /// <summary>一键安装 VB-CABLE（解内置包 + 管理员权限跑安装器；装完要重启电脑）。</summary>
    /// <returns>(是否已发起, 人话)。</returns>
    public (bool Ok, string Message) VbCableInstall()
    {
        return _vbCable.Install();
    }

    /// <summary>初始化实时段——读配置、从命令行默认值补路径、把路径推给实时引擎。</summary>
    /// <param name="defaults">默认配置（路径来自命令行选项）。</param>
    /// <param name="dataDir">数据目录（配置落点）。</param>
    private void InitRealtime(RvcConfig defaults, string dataDir)
    {
        _rtConfigPath = Path.Combine(dataDir, "rvc-realtime-config.json");
        _rtConfig = LoadRealtimeConfig();

        if (_rtConfig.PythonPath.Length == 0)
        {
            _rtConfig.PythonPath = defaults.PythonPath;
        }

        if (_rtConfig.RepoDir.Length == 0)
        {
            _rtConfig.RepoDir = defaults.RepoDir;
        }

        if (_rtConfig.ModelsRoot.Length == 0)
        {
            _rtConfig.ModelsRoot = defaults.ModelsRoot;
        }

        if (_rtConfig.Device.Length == 0)
        {
            _rtConfig.Device = defaults.Device;
        }

        ApplyRealtimeEnginePaths();
    }

    /// <summary>把实时配置里的路径推给实时引擎（参数没变就什么都不做）。</summary>
    private void ApplyRealtimeEnginePaths()
    {
        string python;
        string repo;
        string models;
        string device;
        bool half;
        lock (_rtGate)
        {
            python = _rtConfig.PythonPath;
            repo = _rtConfig.RepoDir;
            models = _rtConfig.ModelsRoot;
            device = _rtConfig.Device;
            half = _rtConfig.Half;
        }

        _rtEngine.Prepare(python, repo, models, device, half);
    }

    /// <summary>实时环境探针（只看磁盘，不启动进程）。</summary>
    /// <returns>环境实况。</returns>
    private RvcRealtimeEnvSnapshot ProbeRealtime()
    {
        RvcRealtimeConfig cfg = RealtimeConfig();
        var blockers = new List<string>();

        string python = cfg.PythonPath;
        bool pythonOk = python.Length > 0 && File.Exists(python);
        if (!pythonOk)
        {
            blockers.Add("解释器不在位：" + (python.Length > 0 ? python : "未配置"));
        }

        string sitePackages = "";
        if (python.Length > 0)
        {
            string? embedRoot = Path.GetDirectoryName(python);
            if (embedRoot != null)
            {
                sitePackages = Path.Combine(embedRoot, "Lib", "site-packages");
            }
        }

        bool repoOk = cfg.RepoDir.Length > 0 && File.Exists(Path.Combine(cfg.RepoDir, "rvc_lite", "hubert.py"));
        if (!repoOk)
        {
            blockers.Add("推理代码不完整（缺 rvc_lite\\hubert.py）：" + cfg.RepoDir);
        }

        string script = Path.Combine(AppContext.BaseDirectory, "data", "rvc", "rvc_realtime_worker.py");
        bool scriptOk = File.Exists(script) || _rtWorkerSource.Length > 0;
        if (!scriptOk)
        {
            blockers.Add("worker 脚本不在位：" + script + "（线 2 的 RvcRealtimeWorkerScript 尚未落地）");
        }

        bool soundDeviceOk = ModulePresent(sitePackages, "sounddevice");
        if (!soundDeviceOk)
        {
            blockers.Add("Python 依赖缺：sounddevice（实时链前提，装进 " + sitePackages + "）");
        }

        string torchGate = cfg.RepoDir.Length > 0 ? Path.Combine(cfg.RepoDir, "torchgate.py") : "";
        bool torchGateOk = torchGate.Length > 0 && File.Exists(torchGate);
        if (!torchGateOk)
        {
            blockers.Add("降噪实现不在位：" + (torchGate.Length > 0 ? torchGate : "runtime\\rvc\\torchgate.py"));
        }

        int modelCount = CountFiles(Path.Combine(cfg.ModelsRoot, "weights"), "*.pth");
        if (modelCount == 0)
        {
            blockers.Add("音色模型目录里没有 .pth：" + Path.Combine(cfg.ModelsRoot, "weights"));
        }

        return new RvcRealtimeEnvSnapshot
        {
            PythonOk = pythonOk,
            RepoOk = repoOk,
            ScriptOk = scriptOk,
            SoundDeviceOk = soundDeviceOk,
            TorchGateOk = torchGateOk,
            ModelCount = modelCount,
            CanStart = blockers.Count == 0,
            Blockers = blockers,
            CheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };
    }

    /// <summary>保存实时配置（归一化 + 落盘 + 推路径给引擎）。</summary>
    /// <param name="next">新配置。</param>
    /// <returns>落盘后的配置副本。</returns>
    private RvcRealtimeConfig SaveRealtimeConfig(RvcRealtimeConfig next)
    {
        lock (_rtGate)
        {
            _rtConfig.HostApiIndex = next.HostApiIndex;
            _rtConfig.Input = CloneDevice(next.Input);
            _rtConfig.Output = CloneDevice(next.Output);
            _rtConfig.Exclusive = next.Exclusive;

            if (next.SampleRate <= 0)
            {
                _rtConfig.SampleRate = 0;
            }
            else
            {
                _rtConfig.SampleRate = Clamp(next.SampleRate, 8000, 192000);
            }

            if (next.Model != null)
            {
                _rtConfig.Model = next.Model;
            }

            _rtConfig.Sid = Clamp(next.Sid, 0, 200);
            _rtConfig.F0UpKey = Clamp(next.F0UpKey, -24, 24);
            _rtConfig.Formant = Clamp(next.Formant, -24, 24);
            _rtConfig.IndexRate = Clamp(next.IndexRate, 0, 1);
            _rtConfig.Threshold = Clamp(next.Threshold, -60, 0);
            _rtConfig.RmsMixRate = Clamp(next.RmsMixRate, 0, 1);
            _rtConfig.Protect = Clamp(next.Protect, 0, 0.5);
            _rtConfig.F0Method = NormalizeF0(next.F0Method);
            _rtConfig.BlockTime = Clamp(next.BlockTime, 0.05, 1);
            _rtConfig.Crossfade = Clamp(next.Crossfade, 0.01, 0.2);
            _rtConfig.ExtraTime = Clamp(next.ExtraTime, 0.5, 5);
            _rtConfig.InputDenoise = next.InputDenoise;
            _rtConfig.OutputDenoise = next.OutputDenoise;
            _rtConfig.UsePv = next.UsePv;
            _rtConfig.HarvestProcesses = Clamp(next.HarvestProcesses, 1, 8);
            _rtConfig.Bypass = next.Bypass;
            _rtConfig.VcBridge = next.VcBridge;
            _rtConfig.VcBridgeBlocks = Clamp(next.VcBridgeBlocks <= 0 ? 1 : next.VcBridgeBlocks, 1, 16);
            if (next.VcBridgeModel != null)
            {
                _rtConfig.VcBridgeModel = next.VcBridgeModel.Trim();
            }

            _rtConfig.VcBridgeDst = Clamp(next.VcBridgeDst, 0, 4096);
            _rtConfig.VcBridgeAutoSrc = next.VcBridgeAutoSrc;
            _rtConfig.VcBridgeAutoF0 = next.VcBridgeAutoF0;

            if (next.Device != null && next.Device.Length > 0)
            {
                _rtConfig.Device = next.Device;
            }

            _rtConfig.Half = next.Half;

            if (next.PythonPath != null && next.PythonPath.Length > 0)
            {
                _rtConfig.PythonPath = next.PythonPath;
            }

            if (next.RepoDir != null && next.RepoDir.Length > 0)
            {
                _rtConfig.RepoDir = next.RepoDir;
            }

            if (next.ModelsRoot != null && next.ModelsRoot.Length > 0)
            {
                _rtConfig.ModelsRoot = next.ModelsRoot;
            }
        }

        SaveRealtimeConfigFile();
        ApplyRealtimeEnginePaths();
        return RealtimeConfig();
    }

    /// <summary>哪些字段改了必须重开流（其余走热更）。</summary>
    /// <param name="before">改前配置。</param>
    /// <param name="after">改后配置。</param>
    /// <returns>需要重开流返回 true。</returns>
    private static bool RealtimeNeedsRestart(RvcRealtimeConfig before, RvcRealtimeConfig after)
    {
        if (before.HostApiIndex != after.HostApiIndex)
        {
            return true;
        }

        if (DeviceKey(before.Input) != DeviceKey(after.Input))
        {
            return true;
        }

        if (DeviceKey(before.Output) != DeviceKey(after.Output))
        {
            return true;
        }

        if (before.Exclusive != after.Exclusive)
        {
            return true;
        }

        if (before.SampleRate != after.SampleRate)
        {
            return true;
        }

        if (before.Model != after.Model)
        {
            return true;
        }

        if (before.Sid != after.Sid)
        {
            return true;
        }

        if (before.F0Method != after.F0Method)
        {
            return true;
        }

        if (before.Device != after.Device)
        {
            return true;
        }

        if (before.Half != after.Half)
        {
            return true;
        }

        if (before.UsePv != after.UsePv)
        {
            return true;
        }

        if (Math.Abs(before.BlockTime - after.BlockTime) > 0.0001)
        {
            return true;
        }

        if (Math.Abs(before.Crossfade - after.Crossfade) > 0.0001)
        {
            return true;
        }

        if (Math.Abs(before.ExtraTime - after.ExtraTime) > 0.0001)
        {
            return true;
        }

        if (before.HarvestProcesses != after.HarvestProcesses)
        {
            return true;
        }

        if (Math.Abs(before.RmsMixRate - after.RmsMixRate) > 0.0001)
        {
            return true;
        }

        if (Math.Abs(before.Protect - after.Protect) > 0.0001)
        {
            return true;
        }

        if (before.PythonPath != after.PythonPath)
        {
            return true;
        }

        if (before.RepoDir != after.RepoDir)
        {
            return true;
        }

        if (before.ModelsRoot != after.ModelsRoot)
        {
            return true;
        }

        return false;
    }

    /// <summary>设备比较键（索引 + 名字双份——名字变但索引同也算变）。</summary>
    /// <param name="device">设备引用。</param>
    /// <returns>比较键。</returns>
    private static string DeviceKey(RvcRealtimeDevice device)
    {
        if (device == null)
        {
            return "";
        }

        return device.Index.ToString() + "|" + (device.Name ?? "");
    }

    /// <summary>实时配置的深拷贝。</summary>
    /// <param name="src">源配置。</param>
    /// <returns>副本。</returns>
    private static RvcRealtimeConfig CloneRealtimeConfig(RvcRealtimeConfig src)
    {
        var copy = new RvcRealtimeConfig
        {
            HostApiIndex = src.HostApiIndex,
            Input = CloneDevice(src.Input),
            Output = CloneDevice(src.Output),
            Exclusive = src.Exclusive,
            SampleRate = src.SampleRate,
            Model = src.Model,
            Sid = src.Sid,
            F0UpKey = src.F0UpKey,
            Formant = src.Formant,
            IndexRate = src.IndexRate,
            Threshold = src.Threshold,
            RmsMixRate = src.RmsMixRate,
            Protect = src.Protect,
            F0Method = src.F0Method,
            BlockTime = src.BlockTime,
            Crossfade = src.Crossfade,
            ExtraTime = src.ExtraTime,
            InputDenoise = src.InputDenoise,
            OutputDenoise = src.OutputDenoise,
            UsePv = src.UsePv,
            HarvestProcesses = src.HarvestProcesses,
            Bypass = src.Bypass,
            VcBridge = src.VcBridge,
            VcBridgeBlocks = src.VcBridgeBlocks,
            VcBridgeModel = src.VcBridgeModel,
            VcBridgeDst = src.VcBridgeDst,
            VcBridgeAutoSrc = src.VcBridgeAutoSrc,
            VcBridgeAutoF0 = src.VcBridgeAutoF0,
            Device = src.Device,
            Half = src.Half,
            PythonPath = src.PythonPath,
            RepoDir = src.RepoDir,
            ModelsRoot = src.ModelsRoot,
        };
        return copy;
    }

    /// <summary>设备引用的深拷贝。</summary>
    /// <param name="src">源设备。</param>
    /// <returns>副本。</returns>
    private static RvcRealtimeDevice CloneDevice(RvcRealtimeDevice src)
    {
        var copy = new RvcRealtimeDevice();
        if (src == null)
        {
            return copy;
        }

        copy.Index = src.Index;
        if (src.Name != null)
        {
            copy.Name = src.Name;
        }

        copy.Cable = src.Cable;
        return copy;
    }

    /// <summary>读实时配置（没有或坏了给默认值）。</summary>
    /// <returns>配置。</returns>
    private RvcRealtimeConfig LoadRealtimeConfig()
    {
        try
        {
            if (!File.Exists(_rtConfigPath))
            {
                return new RvcRealtimeConfig();
            }

            string json = File.ReadAllText(_rtConfigPath);
            RvcRealtimeConfig? loaded = JsonSerializer.Deserialize<RvcRealtimeConfig>(json, JsonOptions());
            if (loaded == null)
            {
                return new RvcRealtimeConfig();
            }

            if (loaded.Input == null)
            {
                loaded.Input = new RvcRealtimeDevice();
            }

            if (loaded.Output == null)
            {
                loaded.Output = new RvcRealtimeDevice();
            }

            return loaded;
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
        {
            return new RvcRealtimeConfig();
        }
    }

    /// <summary>写实时配置（失败具名出声，不静默）。</summary>
    private void SaveRealtimeConfigFile()
    {
        try
        {
            string json;
            lock (_rtGate)
            {
                json = JsonSerializer.Serialize(_rtConfig, JsonOptions());
            }

            File.WriteAllText(_rtConfigPath, json);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            Console.Error.WriteLine("写 rvc-realtime-config.json 失败：" + ex.Message);
        }
    }
}
