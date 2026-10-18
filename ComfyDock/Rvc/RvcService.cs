using System;
using System.Collections.Generic;
using System.IO;
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
            };
        }
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
        lock (_gate)
        {
            if (_job.State == "submitted" || _job.State == "running")
            {
                return Task.FromResult((false, "上一段还在跑——先等它出来，或点「停止引擎」"));
            }

            if (_config.Model.Length == 0)
            {
                return Task.FromResult((false, "没选音色模型"));
            }

            string input = (_config.InputPath ?? "").Trim();
            if (input.Length == 0 || !File.Exists(input))
            {
                return Task.FromResult((false, "输入音频不在位：" + (input.Length == 0 ? "未填" : input)));
            }

            snapshot = new RvcConfig
            {
                Model = _config.Model,
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

            _job = new RvcJobStatus
            {
                Id = DateTime.Now.ToString("HHmmss"),
                State = "submitted",
                Message = "已提交，等引擎接手…",
                InputPreview = Path.GetFileName(input),
                Model = snapshot.Model,
                SubmittedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };
        }

        _ = Task.Run(() => RunJobAsync(snapshot));
        return Task.FromResult((true, "已提交（音色 " + Path.GetFileNameWithoutExtension(snapshot.Model)
            + " · F0 " + snapshot.F0Method + " · 变调 " + snapshot.F0UpKey.ToString("0.#") + "）"));
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
                _job.Message = "已停引擎（这一段作废）";
                _job.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
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

    /// <summary>后台跑一次变声，结束后更新任务状态。</summary>
    /// <param name="cfg">本次参数快照。</param>
    /// <returns>无。</returns>
    private async Task RunJobAsync(RvcConfig cfg)
    {
        lock (_gate)
        {
            _job.State = "running";
            _job.Message = "变声中…";
        }

        DateTime started = DateTime.Now;
        string outName = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_"
            + Path.GetFileNameWithoutExtension(cfg.InputPath) + "_"
            + Path.GetFileNameWithoutExtension(cfg.Model) + ".wav";
        string outPath = Path.Combine(_outDir, outName);

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

        (bool ok, string file, double audioSeconds, string message) = await _engine.RunAsync(
            cfg.InputPath,
            cfg.Model,
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

        lock (_gate)
        {
            _job.State = ok ? "done" : "error";
            _job.Message = message;
            _job.File = ok ? file : "";
            _job.AudioSeconds = audioSeconds;
            _job.ElapsedSeconds = Math.Round((DateTime.Now - started).TotalSeconds, 2);
            _job.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
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
}
