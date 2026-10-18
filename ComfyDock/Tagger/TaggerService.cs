using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Tagger;

/// <summary>
/// 本地生成（WD Tagger 打标）的持久化配置——参数 + 这一路要用的路径（落盘 <c>data\tagger-config.json</c>）。
/// </summary>
public sealed class TaggerConfig
{
    /// <summary>模型 id（索引内；空 = 首推模型）。</summary>
    public string ModelId { get; set; } = "";

    /// <summary>待打标路径——图片文件，或一个目录（取目录里一层图片）。</summary>
    public string InputPath { get; set; } = "";

    /// <summary>通用标签阈值（官方默认 0.35）。</summary>
    public double GeneralThreshold { get; set; } = 0.35;

    /// <summary>角色标签阈值（官方默认 0.85）。</summary>
    public double CharacterThreshold { get; set; } = 0.85;

    /// <summary>通用标签用 MCut 自适应阈值。</summary>
    public bool GeneralMcut { get; set; }

    /// <summary>角色标签用 MCut 自适应阈值（下限 0.15）。</summary>
    public bool CharacterMcut { get; set; }

    /// <summary>结果落成同名 .txt（与图片同目录）。</summary>
    public bool WriteText { get; set; } = true;

    /// <summary>推理设备（auto / cuda / cpu）。</summary>
    public string Device { get; set; } = "auto";

    /// <summary>ComfyUI 根目录（模型与解释器都从它派生）。</summary>
    public string ComfyRoot { get; set; } = "";
}

/// <summary>
/// 本机打标环境实况——解释器 / 推理依赖 / 模型两件在不在。全部来自磁盘实测。
/// </summary>
public sealed class TaggerEnvSnapshot
{
    /// <summary>解释器在位（python_embeded\python.exe）。</summary>
    public bool PythonOk { get; set; }

    /// <summary>onnxruntime 在位。</summary>
    public bool OnnxRuntimeOk { get; set; }

    /// <summary>numpy 在位。</summary>
    public bool NumpyOk { get; set; }

    /// <summary>PIL 在位。</summary>
    public bool PillowOk { get; set; }

    /// <summary>onnxruntime 版本（读不到为空串）。</summary>
    public string OnnxRuntimeVersion { get; set; } = "";

    /// <summary>权重在位。</summary>
    public bool ModelOk { get; set; }

    /// <summary>标签表在位。</summary>
    public bool LabelsOk { get; set; }

    /// <summary>权重完整路径。</summary>
    public string ModelPath { get; set; } = "";

    /// <summary>标签表完整路径。</summary>
    public string LabelsPath { get; set; } = "";

    /// <summary>权重字节数。</summary>
    public long ModelBytes { get; set; }

    /// <summary>标签表字节数。</summary>
    public long LabelsBytes { get; set; }

    /// <summary>能不能打标。</summary>
    public bool CanGenerate { get; set; }

    /// <summary>缺什么（人话，逐条）。</summary>
    public List<string> Blockers { get; set; } = new List<string>();

    /// <summary>探测时刻。</summary>
    public string CheckedAt { get; set; } = "";
}

/// <summary>
/// 本地生成板块（WD Tagger 打标）的服务面——环境探针 + 配置 + 模型索引与下载 + 常驻引擎投递 + 结果清单。
/// <para>与 MOSS-Image / TTS 平行：模型住本机 ComfyUI 的模型目录，推理借 ComfyUI 的 python_embeded 跑常驻 worker，
/// 不启 ComfyUI 服务、不开端口。</para>
/// </summary>
public sealed class TaggerService
{
    /// <summary>可打标的图片扩展名。</summary>
    private static readonly string[] ImageExtensions =
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff",
    };

    /// <summary>配置落盘路径。</summary>
    private readonly string _configPath;

    /// <summary>状态锁。</summary>
    private readonly object _gate = new object();

    /// <summary>常驻引擎。</summary>
    private readonly WdTaggerEngine _engine = new WdTaggerEngine();

    /// <summary>下载器。</summary>
    private readonly TaggerDownloader _downloader = new TaggerDownloader();

    /// <summary>当前配置。</summary>
    private TaggerConfig _config;

    /// <summary>当前任务状态。</summary>
    private TaggerJobStatus _job = new TaggerJobStatus();

    /// <summary>取消源（停止当前批次）。</summary>
    private CancellationTokenSource? _cts;

    /// <summary>以默认配置构造。</summary>
    /// <param name="defaults">默认配置（路径来自命令行选项）。</param>
    /// <param name="dataDir">数据目录（配置落点）。</param>
    public TaggerService(TaggerConfig defaults, string dataDir)
    {
        _configPath = Path.Combine(dataDir, "tagger-config.json");
        _config = LoadConfig();

        if (_config.ComfyRoot.Length == 0)
        {
            _config.ComfyRoot = defaults.ComfyRoot;
        }

        // 模型固定一套——配置里残留的旧 id 一律归一
        _config.ModelId = TaggerCatalog.ModelId;

        _downloader.Start();
        ApplyEnginePaths();
    }

    /// <summary>常驻引擎状态（面板显示这一路的死活）。</summary>
    public TaggerEngineState Engine => _engine.State;

    /// <summary>当前任务状态（拷贝，防外部改）。</summary>
    /// <returns>任务状态副本。</returns>
    public TaggerJobStatus Job()
    {
        lock (_gate)
        {
            return CloneJob(_job);
        }
    }

    /// <summary>下载任务清单（面板轮询用）。</summary>
    /// <returns>任务列表（新→旧）。</returns>
    public List<TaggerDownloadJob> Downloads() => _downloader.Snapshot();

    /// <summary>面板快照——环境 + 模型索引 + 配置 + 下载 + 任务 + 引擎。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>快照对象（匿名结构，直接序列化给前端）。</returns>
    public Task<object> SnapshotAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TaggerEnvSnapshot env = Probe();

        var models = new List<object>();
        foreach (TaggerModel model in TaggerCatalog.Models)
        {
            var files = new List<object>();
            bool ready = true;
            foreach (TaggerFile file in model.Files)
            {
                string path = PathOf(file);
                long size = 0;
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists)
                    {
                        size = info.Length;
                    }
                }
                catch (IOException)
                {
                    size = 0;
                }

                bool present = size > 0 && (file.Bytes <= 0 || size == file.Bytes);
                if (!present)
                {
                    ready = false;
                }

                files.Add(new
                {
                    id = file.Id,
                    title = file.Title,
                    note = file.Note,
                    repo = file.Repo,
                    repoPath = file.RepoPath,
                    bytes = file.Bytes,
                    present,
                    path,
                    actualBytes = size,
                });
            }

            models.Add(new
            {
                id = model.Id,
                title = model.Title,
                repo = model.Repo,
                note = model.Note,
                bytes = model.TotalBytes(),
                ready,
                files,
            });
        }

        object snapshot = new
        {
            env = new
            {
                pythonOk = env.PythonOk,
                onnxRuntimeOk = env.OnnxRuntimeOk,
                numpyOk = env.NumpyOk,
                pillowOk = env.PillowOk,
                onnxRuntimeVersion = env.OnnxRuntimeVersion,
                modelOk = env.ModelOk,
                labelsOk = env.LabelsOk,
                modelPath = env.ModelPath,
                labelsPath = env.LabelsPath,
                modelBytes = env.ModelBytes,
                labelsBytes = env.LabelsBytes,
                canGenerate = env.CanGenerate,
                blockers = env.Blockers,
                checkedAt = env.CheckedAt,
            },
            config = _config,
            models,
            job = Job(),
            engine = Engine,
            downloads = Downloads(),
            comfyRoot = _config.ComfyRoot,
            dataDir = Path.Combine(AppContext.BaseDirectory, "data", "tagger"),
            configError = LastConfigError,
        };

        return Task.FromResult(snapshot);
    }

    /// <summary>改配置（传进来的非空字段覆盖，其余不动）。</summary>
    /// <param name="next">新配置。</param>
    /// <returns>落盘后的配置。</returns>
    public TaggerConfig UpdateConfig(TaggerConfig next)
    {
        lock (_gate)
        {
            _config.ModelId = TaggerCatalog.ModelId;
            _config.InputPath = next.InputPath ?? "";
            _config.GeneralThreshold = Clamp(next.GeneralThreshold, 0.0, 1.0);
            _config.CharacterThreshold = Clamp(next.CharacterThreshold, 0.0, 1.0);
            _config.GeneralMcut = next.GeneralMcut;
            _config.CharacterMcut = next.CharacterMcut;
            _config.WriteText = next.WriteText;
            _config.Device = next.Device == "cuda" || next.Device == "cpu" ? next.Device : "auto";

            if (next.ComfyRoot.Length > 0)
            {
                _config.ComfyRoot = next.ComfyRoot;
            }

            SaveConfig();
        }

        ApplyEnginePaths();
        return _config;
    }

    /// <summary>按当前配置投递一次打标（立刻返回，跑在后台，面板轮询状态）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否受理, 说明)。</returns>
    public Task<(bool Ok, string Message)> GenerateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        TaggerModel? model = TaggerCatalog.DefaultModel();
        if (model is null)
        {
            return Task.FromResult((false, "模型索引为空"));
        }

        TaggerEnvSnapshot env = Probe();
        if (!env.CanGenerate)
        {
            string reason = env.Blockers.Count > 0 ? env.Blockers[0] : "前置条件不足";
            return Task.FromResult((false, reason));
        }

        List<string> images;
        string inputPath;
        TaggerConfig snapshot;
        lock (_gate)
        {
            if (_job.State == "submitted" || _job.State == "running")
            {
                return Task.FromResult((false, "上一批还在跑——先等它出来，或点「停止」"));
            }

            inputPath = (_config.InputPath ?? "").Trim();
            if (inputPath.Length == 0)
            {
                return Task.FromResult((false, "没选输入——填一张图片或一个目录"));
            }

            images = CollectImages(inputPath, out string walkError);
            if (images.Count == 0)
            {
                return Task.FromResult((false, walkError.Length > 0 ? walkError : "输入里没有可打标的图片：" + inputPath));
            }

            snapshot = new TaggerConfig
            {
                ModelId = model.Id,
                InputPath = inputPath,
                GeneralThreshold = _config.GeneralThreshold,
                CharacterThreshold = _config.CharacterThreshold,
                GeneralMcut = _config.GeneralMcut,
                CharacterMcut = _config.CharacterMcut,
                WriteText = _config.WriteText,
                Device = _config.Device,
                ComfyRoot = _config.ComfyRoot,
            };

            _job = new TaggerJobStatus
            {
                Id = DateTime.Now.ToString("HHmmss"),
                State = "submitted",
                Message = "已提交，等引擎接手…",
                Model = model.Title,
                Total = images.Count,
                SubmittedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };
            _cts = new CancellationTokenSource();
        }

        TaggerModel finalModel = model;
        CancellationToken token = _cts is null ? CancellationToken.None : _cts.Token;
        _ = Task.Run(() => RunJobAsync(snapshot, finalModel, images, token));

        return Task.FromResult((true, "已提交（" + images.Count + " 张，模型 " + model.Title + "）"));
    }

    /// <summary>停掉当前批次与常驻引擎（显存还回去）。</summary>
    /// <returns>说明。</returns>
    public string Stop()
    {
        CancellationTokenSource? cts = _cts;
        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已释放——取消动作本身没有意义，但要让面板看见：把原因记到任务说明里
                lock (_gate)
                {
                    _job.Message = "停止时取消源已释放（这一批可能已自然结束）";
                }
            }
        }

        _engine.Stop();
        lock (_gate)
        {
            if (_job.State == "submitted" || _job.State == "running")
            {
                _job.State = "idle";
                _job.Message = "已停引擎（这批作废）";
                _job.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }
        }

        return "已停引擎";
    }

    /// <summary>入队一条模型文件下载。</summary>
    /// <param name="fileId">索引 id（如 eva02-large:onnx）。</param>
    /// <returns>(是否受理, 说明)。</returns>
    public (bool Ok, string Message) DownloadFile(string fileId)
    {
        TaggerFile? file = TaggerCatalog.FindFile(fileId);
        if (file is null)
        {
            return (false, "索引里没有这个文件：" + fileId);
        }

        if (_config.ComfyRoot.Length == 0)
        {
            return (false, "ComfyUI 根目录没配（先到环境页填路径）");
        }

        string target = PathOf(file);
        if (File.Exists(target) && (file.Bytes <= 0 || new FileInfo(target).Length == file.Bytes))
        {
            return (false, "已在位：" + target);
        }

        if (!_downloader.Enqueue(file, target, out string reason))
        {
            return (false, reason);
        }

        return (true, "已入队：" + file.Title + " → " + target);
    }

    /// <summary>入队唯一模型的全部文件（权重 + 标签表）。</summary>
    /// <returns>(是否受理, 说明)。</returns>
    public (bool Ok, string Message) DownloadModel()
    {
        TaggerModel? model = TaggerCatalog.DefaultModel();
        if (model is null)
        {
            return (false, "模型索引为空");
        }

        int queued = 0;
        var skipped = new List<string>();
        foreach (TaggerFile file in model.Files)
        {
            (bool ok, string message) = DownloadFile(file.Id);
            if (ok)
            {
                queued++;
            }
            else
            {
                skipped.Add(message);
            }
        }

        if (queued == 0)
        {
            return (false, skipped.Count > 0 ? skipped[0] : "没有可下载的文件");
        }

        return (true, "已入队 " + queued + " 个文件（" + model.Title + "）");
    }

    /// <summary>取消一条下载。</summary>
    /// <param name="fileId">索引 id。</param>
    /// <returns>说明。</returns>
    public string CancelDownload(string fileId)
    {
        return _downloader.Cancel(fileId) ? "已请求取消" : "没有这条在途任务";
    }

    /// <summary>任务结果里某张图的产物 txt 完整路径（面板查看用）。</summary>
    /// <param name="path">图片完整路径。</param>
    /// <returns>txt 路径；不存在返回空串。</returns>
    public static string TextPathOf(string path)
    {
        if (path.Length == 0)
        {
            return "";
        }

        string text = Path.ChangeExtension(path, ".txt");
        return File.Exists(text) ? text : "";
    }

    /// <summary>某个索引条目的落盘完整路径。</summary>
    /// <param name="file">索引条目。</param>
    /// <returns>完整路径。</returns>
    public string PathOf(TaggerFile file)
    {
        if (_config.ComfyRoot.Length == 0)
        {
            return "";
        }

        return Path.Combine(_config.ComfyRoot, "models", "onnx", TaggerCatalog.SubDir, file.ModelId, file.FileName);
    }

    /// <summary>环境探针（只看磁盘，不启动进程）。</summary>
    /// <returns>环境快照。</returns>
    private TaggerEnvSnapshot Probe()
    {
        TaggerConfig cfg;
        lock (_gate)
        {
            cfg = _config;
        }

        var blockers = new List<string>();
        string root = cfg.ComfyRoot;
        string installRoot = root.Length > 0
            ? (Path.GetDirectoryName(root.TrimEnd('\\', '/')) ?? root)
            : "";
        string python = installRoot.Length > 0 ? Path.Combine(installRoot, "python_embeded", "python.exe") : "";
        string sitePackages = installRoot.Length > 0
            ? Path.Combine(installRoot, "python_embeded", "Lib", "site-packages")
            : "";

        bool pythonOk = python.Length > 0 && File.Exists(python);
        if (!pythonOk)
        {
            blockers.Add("解释器不在位：" + (python.Length > 0 ? python : "ComfyUI 根目录未配置"));
        }

        bool onnxOk = sitePackages.Length > 0 && Directory.Exists(Path.Combine(sitePackages, "onnxruntime"));
        if (!onnxOk)
        {
            blockers.Add("onnxruntime 不在位（" + sitePackages + "）");
        }

        string onnxVersion = "";
        if (onnxOk)
        {
            onnxVersion = ReadOnnxRuntimeVersion(sitePackages);
        }

        bool numpyOk = sitePackages.Length > 0 && Directory.Exists(Path.Combine(sitePackages, "numpy"));
        if (!numpyOk)
        {
            blockers.Add("numpy 不在位（" + sitePackages + "）");
        }

        bool pillowOk = sitePackages.Length > 0 && Directory.Exists(Path.Combine(sitePackages, "PIL"));
        if (!pillowOk)
        {
            blockers.Add("Pillow（PIL）不在位（" + sitePackages + "）");
        }

        TaggerModel? model = TaggerCatalog.DefaultModel();
        string modelPath = "";
        string labelsPath = "";
        long modelBytes = 0;
        long labelsBytes = 0;
        bool modelOk = false;
        bool labelsOk = false;

        if (model is not null)
        {
            TaggerFile? onnx = model.Onnx();
            TaggerFile? labels = model.Labels();
            if (onnx is not null)
            {
                modelPath = PathOf(onnx);
                modelOk = Measure(modelPath, onnx.Bytes, out modelBytes);
                if (!modelOk)
                {
                    blockers.Add("权重不在位或大小不符：" + modelPath);
                }
            }

            if (labels is not null)
            {
                labelsPath = PathOf(labels);
                labelsOk = Measure(labelsPath, labels.Bytes, out labelsBytes);
                if (!labelsOk)
                {
                    blockers.Add("标签表不在位或大小不符：" + labelsPath);
                }
            }
        }
        else
        {
            blockers.Add("模型索引为空");
        }

        return new TaggerEnvSnapshot
        {
            PythonOk = pythonOk,
            OnnxRuntimeOk = onnxOk,
            NumpyOk = numpyOk,
            PillowOk = pillowOk,
            OnnxRuntimeVersion = onnxVersion,
            ModelOk = modelOk,
            LabelsOk = labelsOk,
            ModelPath = modelPath,
            LabelsPath = labelsPath,
            ModelBytes = modelBytes,
            LabelsBytes = labelsBytes,
            CanGenerate = blockers.Count == 0,
            Blockers = blockers,
            CheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };
    }

    /// <summary>把配置里的路径推给常驻引擎（参数没变就什么都不做）。</summary>
    private void ApplyEnginePaths()
    {
        _engine.Prepare(_config.ComfyRoot);
    }

    /// <summary>后台跑一批打标，结束后更新任务状态。</summary>
    /// <param name="cfg">本次参数快照。</param>
    /// <param name="model">模型。</param>
    /// <param name="images">待打标图片。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>无。</returns>
    private async Task RunJobAsync(TaggerConfig cfg, TaggerModel model, List<string> images, CancellationToken ct)
    {
        TaggerFile? onnx = model.Onnx();
        TaggerFile? labels = model.Labels();
        string modelPath = onnx is null ? "" : PathOf(onnx);
        string labelsPath = labels is null ? "" : PathOf(labels);

        var parameters = new TaggerGenParams
        {
            ModelId = model.Id,
            InputPath = cfg.InputPath,
            GeneralThreshold = cfg.GeneralThreshold,
            CharacterThreshold = cfg.CharacterThreshold,
            GeneralMcut = cfg.GeneralMcut,
            CharacterMcut = cfg.CharacterMcut,
            WriteText = cfg.WriteText,
            Device = cfg.Device,
        };

        lock (_gate)
        {
            _job.State = "running";
            _job.Message = "打标中…";
        }

        DateTime started = DateTime.Now;
        (bool ok, string message, double seconds) = await _engine.RunAsync(
            modelPath,
            labelsPath,
            model.Id,
            images,
            parameters,
            item => OnItem(item),
            (file, reason) => OnFailed(file, reason),
            ct).ConfigureAwait(false);

        lock (_gate)
        {
            _job.State = ok ? "done" : "error";
            _job.Message = message;
            _job.Current = "";
            _job.ElapsedSeconds = Math.Round((DateTime.Now - started).TotalSeconds, 2);
            _job.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }

    /// <summary>单张完成——累加进度与结果。</summary>
    /// <param name="item">结果。</param>
    private void OnItem(TaggerImageResult item)
    {
        lock (_gate)
        {
            _job.Items.Add(item);
            _job.Done = _job.Items.Count;
            _job.Current = item.File;
        }
    }

    /// <summary>单张失败——记进失败清单。</summary>
    /// <param name="file">文件名。</param>
    /// <param name="reason">原因。</param>
    private void OnFailed(string file, string reason)
    {
        lock (_gate)
        {
            _job.Failed.Add(file + "：" + Short(reason));
            _job.Current = file;
        }
    }

    /// <summary>收集待打标图片——给单个文件就只打它，给目录就取一层图片（按文件名排序）。</summary>
    /// <param name="path">输入路径。</param>
    /// <param name="error">失败原因（成功为空串）。</param>
    /// <returns>图片完整路径列表。</returns>
    private static List<string> CollectImages(string path, out string error)
    {
        error = "";
        var list = new List<string>();
        try
        {
            if (File.Exists(path))
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ImageExtensions.Contains(ext))
                {
                    list.Add(path);
                }
                else
                {
                    error = "这个扩展名不在可打标清单里：" + ext;
                }

                return list;
            }

            if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                var files = new List<FileInfo>();
                foreach (FileInfo file in info.GetFiles())
                {
                    if (ImageExtensions.Contains(file.Extension.ToLowerInvariant()))
                    {
                        files.Add(file);
                    }
                }

                files.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                foreach (FileInfo file in files)
                {
                    list.Add(file.FullName);
                }

                if (list.Count == 0)
                {
                    error = "目录里没有可打标的图片（一层，不递归）：" + path;
                }

                return list;
            }

            error = "路径不存在：" + path;
            return list;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            error = "读输入失败：" + ex.Message;
            return list;
        }
    }

    /// <summary>量一个文件的字节数并校验是否与索引相符（索引字节为 0 时只要求非空）。</summary>
    /// <param name="path">路径。</param>
    /// <param name="expected">索引字节数（0 = 未实测）。</param>
    /// <param name="actual">实测字节数。</param>
    /// <returns>在位且大小相符返回 true。</returns>
    private static bool Measure(string path, long expected, out long actual)
    {
        actual = 0;
        if (path.Length == 0)
        {
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return false;
            }

            actual = info.Length;
            if (actual <= 0)
            {
                return false;
            }

            return expected <= 0 || actual == expected;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>读 onnxruntime 的版本号（读 <c>onnxruntime\capi\version_info.py</c> 里的 VERSION 字段太绕——改读目录里的 dist-info）。</summary>
    /// <param name="sitePackages">site-packages 路径。</param>
    /// <returns>版本号；读不到返回空串。</returns>
    public static string ReadOnnxRuntimeVersion(string sitePackages)
    {
        try
        {
            if (!Directory.Exists(sitePackages))
            {
                return "";
            }

            foreach (string dir in Directory.GetDirectories(sitePackages, "onnxruntime-*.dist-info"))
            {
                string name = Path.GetFileName(dir);
                int dash = name.IndexOf('-');
                int end = name.IndexOf(".dist-info", StringComparison.OrdinalIgnoreCase);
                if (dash >= 0 && end > dash)
                {
                    return name.Substring(dash + 1, end - dash - 1);
                }
            }

            // 没有 dist-info 时退回读 __init__ 里的 __version__
            string init = Path.Combine(sitePackages, "onnxruntime", "__init__.py");
            if (File.Exists(init))
            {
                foreach (string line in File.ReadLines(init))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("__version__", StringComparison.Ordinal))
                    {
                        int quote = trimmed.IndexOf('"');
                        int quote2 = trimmed.LastIndexOf('"');
                        if (quote >= 0 && quote2 > quote)
                        {
                            return trimmed.Substring(quote + 1, quote2 - quote - 1);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return "";
        }

        return "";
    }

    /// <summary>读配置（文件不在或坏了就用默认值）。</summary>
    /// <returns>配置。</returns>
    private TaggerConfig LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                string text = File.ReadAllText(_configPath);
                TaggerConfig? cfg = JsonSerializer.Deserialize<TaggerConfig>(text);
                if (cfg is not null)
                {
                    return cfg;
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
        {
            // 配置坏了 / 读不到——回落默认值，但必须出声（回落默认值本身是可观测事件）
            LastConfigError = "读配置失败，已用默认值：" + ex.Message;
        }

        return new TaggerConfig();
    }

    /// <summary>最近一次配置读写的错误（空 = 没有错误）。面板与 CLI 都读它。</summary>
    public string LastConfigError { get; private set; } = "";

    /// <summary>落盘配置。</summary>
    private void SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath) ?? AppContext.BaseDirectory);
            File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // 落盘失败必须可见——内存值仍生效，但下一次快照会带上这条错误
            LastConfigError = "写配置失败：" + ex.Message;
        }
    }

    /// <summary>任务状态深拷贝（防外部改内部集合）。</summary>
    /// <param name="job">源状态。</param>
    /// <returns>副本。</returns>
    private static TaggerJobStatus CloneJob(TaggerJobStatus job)
    {
        var copy = new TaggerJobStatus
        {
            Id = job.Id,
            State = job.State,
            Message = job.Message,
            Model = job.Model,
            Done = job.Done,
            Total = job.Total,
            Current = job.Current,
            ElapsedSeconds = job.ElapsedSeconds,
            SubmittedAt = job.SubmittedAt,
            FinishedAt = job.FinishedAt,
            Failed = new List<string>(job.Failed),
        };

        foreach (TaggerImageResult item in job.Items)
        {
            copy.Items.Add(new TaggerImageResult
            {
                File = item.File,
                Path = item.Path,
                Rating = item.Rating,
                GeneralText = item.GeneralText,
                TextFile = item.TextFile,
                Seconds = item.Seconds,
                Ratings = new List<TaggerTag>(item.Ratings),
                General = new List<TaggerTag>(item.General),
                Characters = new List<TaggerTag>(item.Characters),
            });
        }

        return copy;
    }

    /// <summary>压短错误文本。</summary>
    /// <param name="text">原文。</param>
    /// <returns>≤ 200 字符。</returns>
    private static string Short(string text)
    {
        return text.Length <= 200 ? text : text.Substring(0, 200) + "…";
    }

    /// <summary>夹取数值。</summary>
    /// <param name="value">原值。</param>
    /// <param name="min">下限。</param>
    /// <param name="max">上限。</param>
    /// <returns>夹取后的值。</returns>
    private static double Clamp(double value, double min, double max)
    {
        if (double.IsNaN(value))
        {
            return min;
        }

        return value < min ? min : (value > max ? max : value);
    }
}
