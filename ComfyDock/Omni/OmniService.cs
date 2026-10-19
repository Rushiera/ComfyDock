using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Omni;

/// <summary>
/// 本地生成（Qwen3-Omni 转写）的持久化配置——转写参数 + 这一路要用的路径（落盘 <c>data\omni-config.json</c>）。
/// </summary>
public sealed class OmniConfig
{
    /// <summary>待转写输入——音频文件，或一个音频目录（只取一层）。</summary>
    public string InputPath { get; set; } = "";

    /// <summary>提示词（决定输出格式；空 = 内置默认）。</summary>
    public string Prompt { get; set; } = "";

    /// <summary>上下文长度（输入与输出共用，默认 65536）。</summary>
    public int Ctx { get; set; } = 65536;

    /// <summary>生成上限（token；0 = 按音频时长自动）。</summary>
    public int MaxTokens { get; set; }

    /// <summary>MoE 专家权重留 CPU 的层数（默认 48——30B 装进 8 G 卡的打法）。</summary>
    public int Ncmoe { get; set; } = 48;

    /// <summary>层放显存（默认 99）。</summary>
    public int Ngl { get; set; } = 99;

    /// <summary>结果落成同名 .txt（落 <c>out\omni</c>）。</summary>
    public bool SaveText { get; set; } = true;

    /// <summary>引擎可执行文件（llama-mtmd-cli.exe）。</summary>
    public string CliPath { get; set; } = "";

    /// <summary>主模型（Qwen3-Omni-30B-A3B-Instruct Q4_K_M）。</summary>
    public string ModelPath { get; set; } = "";

    /// <summary>音频编码器（mmproj）。</summary>
    public string MmprojPath { get; set; } = "";

    /// <summary>解释器（本机 ComfyUI 的 python_embeded\python.exe，只用来探音频时长）。</summary>
    public string PythonPath { get; set; } = "";
}

/// <summary>
/// 一条音频的转写进度（批量时逐条更新）。
/// </summary>
public sealed class OmniJobItem
{
    /// <summary>音频文件名。</summary>
    public string Name { get; set; } = "";

    /// <summary>状态：pending / running / done / error。</summary>
    public string State { get; set; } = "pending";

    /// <summary>产物 txt 文件名（未落盘为空）。</summary>
    public string File { get; set; } = "";

    /// <summary>正文长度（字）。</summary>
    public int Chars { get; set; }

    /// <summary>音频时长（秒）。</summary>
    public double AudioSeconds { get; set; }

    /// <summary>本条耗时（秒）。</summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>说明 / 失败原因。</summary>
    public string Message { get; set; } = "";
}

/// <summary>
/// 一次转写任务的状态（面板轮询它）。
/// </summary>
public sealed class OmniJobStatus
{
    /// <summary>任务号。</summary>
    public string Id { get; set; } = "";

    /// <summary>状态：idle / submitted / running / done / error。</summary>
    public string State { get; set; } = "idle";

    /// <summary>人话说明。</summary>
    public string Message { get; set; } = "";

    /// <summary>输入路径回显。</summary>
    public string InputPreview { get; set; } = "";

    /// <summary>单条任务的正文（批量时留空，逐条看产物）。</summary>
    public string Text { get; set; } = "";

    /// <summary>本批总条数。</summary>
    public int Total { get; set; }

    /// <summary>已完成条数。</summary>
    public int Done { get; set; }

    /// <summary>失败条数。</summary>
    public int Failed { get; set; }

    /// <summary>引擎报告的生成 token 数。</summary>
    public int GenTokens { get; set; }

    /// <summary>耗时（秒）。</summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>提交时刻。</summary>
    public string SubmittedAt { get; set; } = "";

    /// <summary>结束时刻。</summary>
    public string FinishedAt { get; set; } = "";

    /// <summary>逐条进度。</summary>
    public List<OmniJobItem> Items { get; set; } = new List<OmniJobItem>();
}

/// <summary>
/// 本机转写环境实况——引擎 / 主模型 / 音频编码器 / 解释器在不在。全部来自磁盘实测。
/// </summary>
public sealed class OmniEnvSnapshot
{
    /// <summary>引擎在位。</summary>
    public bool CliOk { get; set; }

    /// <summary>主模型在位。</summary>
    public bool ModelOk { get; set; }

    /// <summary>音频编码器在位。</summary>
    public bool MmprojOk { get; set; }

    /// <summary>解释器在位（只用于探时长；缺了仍可转写，按固定上限）。</summary>
    public bool PythonOk { get; set; }

    /// <summary>主模型字节数。</summary>
    public long ModelBytes { get; set; }

    /// <summary>音频编码器字节数。</summary>
    public long MmprojBytes { get; set; }

    /// <summary>能不能转写。</summary>
    public bool CanGenerate { get; set; }

    /// <summary>缺什么（人话，逐条）。</summary>
    public List<string> Blockers { get; set; } = new List<string>();

    /// <summary>探测时刻。</summary>
    public string CheckedAt { get; set; } = "";
}

/// <summary>
/// 本地生成板块（Qwen3-Omni 转写）的服务面——环境探针 + 配置 + 引擎投递 + 产物清单。
/// <para>与 MOSS-Image / TTS / 打标 / RVC 平行：模型住本机 ComfyUI 的 <c>models\LLM</c>，
/// 引擎是 llama.cpp 的 <c>llama-mtmd-cli</c>，不启 ComfyUI 服务、不开端口、不装运行时。</para>
/// </summary>
public sealed class OmniService
{
    /// <summary>能转写的媒体扩展名——音频直接喂，视频先抽音轨。</summary>
    private static readonly string[] AudioExtensions =
    {
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma", ".amr",
    };

    /// <summary>视频扩展名——先抽 16 kHz 单声道音轨再喂模型。</summary>
    private static readonly string[] VideoExtensions =
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".flv", ".wmv", ".m4v", ".ts", ".mpg", ".mpeg", ".rmvb",
    };

    /// <summary>内置默认提示词——按时间 + 内容的逐字稿（换场景才需要改）。</summary>
    public const string DefaultPrompt =
        "【任务】请把这段音频按时间顺序逐字转写成文字稿，要求："
        + "1. 一行一句，按实际说话顺序，不合并、不概括、不省略；"
        + "2. 每行格式为：[发音开始时间]内容；"
        + "3. 发音开始时间为这句话开始说的时刻，格式[mm:ss]；"
        + "4. 只输出转写正文，不要标题、不要总结、不要说话人标签。";

    /// <summary>单次输入硬上限（秒）——与本地链一致（约 83 分钟）。</summary>
    private const double InputLimitSeconds = 5000.0;

    /// <summary>配置落盘路径。</summary>
    private readonly string _configPath;

    /// <summary>产物落点目录。</summary>
    private readonly string _outDir;

    /// <summary>日志落点目录。</summary>
    private readonly string _logDir;

    /// <summary>视频抽音轨的临时目录。</summary>
    private readonly string _tmpDir;

    /// <summary>状态锁。</summary>
    private readonly object _gate = new object();

    /// <summary>引擎（一次一条，不并发）。</summary>
    private readonly OmniEngine _engine = new OmniEngine();

    /// <summary>当前配置。</summary>
    private OmniConfig _config;

    /// <summary>当前任务状态。</summary>
    private OmniJobStatus _job = new OmniJobStatus();

    /// <summary>以选项构造。</summary>
    /// <param name="defaults">默认配置（路径来自命令行选项）。</param>
    /// <param name="dataDir">数据目录（配置与日志落点）。</param>
    /// <param name="outDir">产物落点目录。</param>
    public OmniService(OmniConfig defaults, string dataDir, string outDir)
    {
        _configPath = Path.Combine(dataDir, "omni-config.json");
        _outDir = outDir;
        _logDir = Path.Combine(dataDir, "omni", "logs");
        _tmpDir = Path.Combine(dataDir, "omni", "tmp");
        _config = LoadConfig();

        if (_config.CliPath.Length == 0)
        {
            _config.CliPath = defaults.CliPath;
        }

        if (_config.ModelPath.Length == 0)
        {
            _config.ModelPath = defaults.ModelPath;
        }

        if (_config.MmprojPath.Length == 0)
        {
            _config.MmprojPath = defaults.MmprojPath;
        }

        if (_config.PythonPath.Length == 0)
        {
            _config.PythonPath = defaults.PythonPath;
        }

        if (_config.Prompt.Trim().Length == 0)
        {
            _config.Prompt = DefaultPrompt;
        }
    }

    /// <summary>产物落点目录。</summary>
    public string OutDir
    {
        get { return _outDir; }
    }

    /// <summary>引擎状态（面板显示死活与阶段）。</summary>
    public object Engine
    {
        get
        {
            return new
            {
                running = _engine.Running,
                pid = _engine.Pid,
                stage = _engine.Stage,
            };
        }
    }

    /// <summary>当前任务状态（拷贝，防外部改）。</summary>
    /// <returns>任务状态副本。</returns>
    public OmniJobStatus Job()
    {
        lock (_gate)
        {
            return CloneJob(_job);
        }
    }

    /// <summary>面板快照——环境 + 配置 + 任务 + 引擎 + 产物。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>快照对象（匿名结构，直接序列化给前端）。</returns>
    public Task<object> SnapshotAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        OmniEnvSnapshot env = Probe();

        object snapshot = new
        {
            env = new
            {
                cliOk = env.CliOk,
                modelOk = env.ModelOk,
                mmprojOk = env.MmprojOk,
                pythonOk = env.PythonOk,
                modelBytes = env.ModelBytes,
                mmprojBytes = env.MmprojBytes,
                canGenerate = env.CanGenerate,
                blockers = env.Blockers,
                checkedAt = env.CheckedAt,
            },
            config = _config,
            job = Job(),
            engine = Engine,
            outputs = ListOutputs(),
            outDir = _outDir,
            defaultPrompt = DefaultPrompt,
        };

        return Task.FromResult(snapshot);
    }

    /// <summary>改配置（传进来的字段覆盖，路径为空时保留原值）。</summary>
    /// <param name="next">新配置。</param>
    /// <returns>落盘后的配置。</returns>
    public OmniConfig UpdateConfig(OmniConfig next)
    {
        lock (_gate)
        {
            _config.InputPath = (next.InputPath ?? "").Trim();
            _config.Prompt = next.Prompt ?? "";
            if (_config.Prompt.Trim().Length == 0)
            {
                _config.Prompt = DefaultPrompt;
            }

            _config.Ctx = Clamp(next.Ctx, 2048, 65536);
            _config.MaxTokens = next.MaxTokens <= 0 ? 0 : Clamp(next.MaxTokens, 512, 65536);
            _config.Ncmoe = Clamp(next.Ncmoe, 0, 99);
            _config.Ngl = Clamp(next.Ngl, 0, 999);
            _config.SaveText = next.SaveText;

            if (next.CliPath.Length > 0)
            {
                _config.CliPath = next.CliPath;
            }

            if (next.ModelPath.Length > 0)
            {
                _config.ModelPath = next.ModelPath;
            }

            if (next.MmprojPath.Length > 0)
            {
                _config.MmprojPath = next.MmprojPath;
            }

            if (next.PythonPath.Length > 0)
            {
                _config.PythonPath = next.PythonPath;
            }

            SaveConfig();
        }

        return _config;
    }

    /// <summary>按当前配置投递一次转写（立刻返回，跑在后台，面板轮询状态）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否受理, 说明)。</returns>
    public Task<(bool Ok, string Message)> GenerateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        OmniEnvSnapshot env = Probe();
        if (!env.CanGenerate)
        {
            string reason = env.Blockers.Count > 0 ? env.Blockers[0] : "前置条件不足";
            return Task.FromResult((false, reason));
        }

        OmniConfig snapshot;
        List<string> files;
        lock (_gate)
        {
            if (_job.State == "submitted" || _job.State == "running")
            {
                return Task.FromResult((false, "上一批还在跑——先等它出来，或点「停止」"));
            }

            files = InputFiles(_config.InputPath);
            if (files.Count == 0)
            {
                return Task.FromResult((false, "输入音频不在位：" + (_config.InputPath.Length == 0 ? "未填" : _config.InputPath)));
            }

            snapshot = new OmniConfig
            {
                InputPath = _config.InputPath,
                Prompt = _config.Prompt,
                Ctx = _config.Ctx,
                MaxTokens = _config.MaxTokens,
                Ncmoe = _config.Ncmoe,
                Ngl = _config.Ngl,
                SaveText = _config.SaveText,
                CliPath = _config.CliPath,
                ModelPath = _config.ModelPath,
                MmprojPath = _config.MmprojPath,
                PythonPath = _config.PythonPath,
            };

            var job = new OmniJobStatus
            {
                Id = DateTime.Now.ToString("HHmmss"),
                State = "submitted",
                Message = "已提交，等引擎接手…",
                InputPreview = files.Count == 1 ? Path.GetFileName(files[0]) : (_config.InputPath + "（" + files.Count + " 条）"),
                Total = files.Count,
                SubmittedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };

            foreach (string file in files)
            {
                job.Items.Add(new OmniJobItem { Name = Path.GetFileName(file) });
            }

            _job = job;
        }

        _ = Task.Run(() => RunBatchAsync(snapshot, files));
        return Task.FromResult((true, "已提交 " + files.Count + " 条（引擎首次加载约 30~60 秒）"));
    }

    /// <summary>停掉在跑的引擎（在跑的这一条作废）。</summary>
    /// <returns>说明。</returns>
    public string Stop()
    {
        bool stopped = _engine.Stop();
        lock (_gate)
        {
            if (_job.State == "submitted" || _job.State == "running")
            {
                _job.State = "idle";
                _job.Message = stopped ? "已停引擎（这一条作废）" : "没有在跑的引擎";
                _job.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }
        }

        return stopped ? "已停引擎" : "没有在跑的引擎";
    }

    /// <summary>产物目录里的 txt 清单（新→旧）。</summary>
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

            FileInfo[] files = new DirectoryInfo(_outDir).GetFiles("*.txt");
            Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            foreach (FileInfo file in files)
            {
                list.Add(new
                {
                    name = file.Name,
                    bytes = file.Length,
                    at = file.LastWriteTime.ToString("MM-dd HH:mm:ss"),
                });

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

    /// <summary>产物 txt 的完整路径（面板查看用）。</summary>
    /// <param name="name">文件名。</param>
    /// <returns>完整路径；越界或不存在返回空串。</returns>
    public string OutputPath(string name)
    {
        if (name.Length == 0
            || name.Contains("..", StringComparison.Ordinal)
            || name.Contains('/', StringComparison.Ordinal)
            || name.Contains('\\', StringComparison.Ordinal)
            || !name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        string path = Path.Combine(_outDir, name);
        return File.Exists(path) ? path : "";
    }

    /// <summary>环境探针（只看磁盘，不启动进程）。</summary>
    /// <returns>环境快照。</returns>
    private OmniEnvSnapshot Probe()
    {
        OmniConfig cfg;
        lock (_gate)
        {
            cfg = _config;
        }

        var blockers = new List<string>();

        bool cliOk = cfg.CliPath.Length > 0 && File.Exists(cfg.CliPath);
        if (!cliOk)
        {
            blockers.Add("引擎不在位：" + (cfg.CliPath.Length > 0 ? cfg.CliPath : "未配置"));
        }

        long modelBytes = 0;
        bool modelOk = cfg.ModelPath.Length > 0 && File.Exists(cfg.ModelPath);
        if (modelOk)
        {
            modelBytes = new FileInfo(cfg.ModelPath).Length;
        }
        else
        {
            blockers.Add("主模型不在位：" + (cfg.ModelPath.Length > 0 ? cfg.ModelPath : "未配置"));
        }

        long mmprojBytes = 0;
        bool mmprojOk = cfg.MmprojPath.Length > 0 && File.Exists(cfg.MmprojPath);
        if (mmprojOk)
        {
            mmprojBytes = new FileInfo(cfg.MmprojPath).Length;
        }
        else
        {
            blockers.Add("音频编码器不在位：" + (cfg.MmprojPath.Length > 0 ? cfg.MmprojPath : "未配置"));
        }

        bool pythonOk = cfg.PythonPath.Length > 0 && File.Exists(cfg.PythonPath);

        return new OmniEnvSnapshot
        {
            CliOk = cliOk,
            ModelOk = modelOk,
            MmprojOk = mmprojOk,
            PythonOk = pythonOk,
            ModelBytes = modelBytes,
            MmprojBytes = mmprojBytes,
            CanGenerate = blockers.Count == 0,
            Blockers = blockers,
            CheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };
    }

    /// <summary>输入路径 → 媒体文件清单（目录只取一层，不递归；音频与视频都收）。</summary>
    /// <param name="input">媒体文件或目录。</param>
    /// <returns>文件清单（排序后）。</returns>
    private static List<string> InputFiles(string input)
    {
        var list = new List<string>();
        string path = (input ?? "").Trim();
        if (path.Length == 0)
        {
            return list;
        }

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
                if (Array.IndexOf(AudioExtensions, ext) >= 0 || Array.IndexOf(VideoExtensions, ext) >= 0)
                {
                    list.Add(file);
                }
            }
        }
        catch (IOException)
        {
            return list;
        }
        catch (UnauthorizedAccessException)
        {
            return list;
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>是不是视频（视频要先抽音轨才能喂模型）。</summary>
    /// <param name="path">文件路径。</param>
    /// <returns>视频返回 true。</returns>
    private static bool IsVideo(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return Array.IndexOf(VideoExtensions, ext) >= 0;
    }

    /// <summary>后台跑一批转写，逐条更新任务状态。</summary>
    /// <param name="cfg">本次参数快照。</param>
    /// <param name="files">音频清单。</param>
    /// <returns>无。</returns>
    private async Task RunBatchAsync(OmniConfig cfg, List<string> files)
    {
        DateTime started = DateTime.Now;
        lock (_gate)
        {
            _job.State = "running";
            _job.Message = "转写中…";
        }

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

        for (int i = 0; i < files.Count; i++)
        {
            string file = files[i];
            string name = Path.GetFileName(file);
            DateTime itemStarted = DateTime.Now;
            bool video = IsVideo(file);

            lock (_gate)
            {
                _job.Message = "第 " + (i + 1) + "/" + files.Count + " 条：" + name;
                if (i < _job.Items.Count)
                {
                    _job.Items[i].State = "running";
                }
            }

            // [段1] 探时长与音轨——视频必须带音轨，否则无法转写（出声，不静默跳过）
            (double duration, bool hasAudio) = OmniEngine.ProbeMedia(cfg.PythonPath, file);
            if (video && !hasAudio)
            {
                lock (_gate)
                {
                    if (i < _job.Items.Count)
                    {
                        _job.Items[i].State = "error";
                        _job.Items[i].Message = "视频没有音轨——无法转写";
                    }

                    _job.Failed = _job.Failed + 1;
                }

                continue;
            }

            if (duration > InputLimitSeconds)
            {
                lock (_gate)
                {
                    if (i < _job.Items.Count)
                    {
                        _job.Items[i].State = "error";
                        _job.Items[i].Message = "媒体 " + duration.ToString("0") + " 秒，超过单次上限 5000 秒——请先切片";
                    }

                    _job.Failed = _job.Failed + 1;
                }

                continue;
            }

            // [段2] 视频先抽音轨——16 kHz 单声道 wav 落临时目录，跑完即删（不碰原始素材）
            string audioPath = file;
            string tempWav = "";
            bool prepared = true;
            string prepareError = "";
            if (video)
            {
                tempWav = Path.Combine(_tmpDir, "omni_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".wav");
                lock (_gate)
                {
                    _job.Message = "第 " + (i + 1) + "/" + files.Count + " 条：" + name + "（抽音轨中…）";
                }

                if (!OmniEngine.ExtractAudio(cfg.PythonPath, file, tempWav))
                {
                    prepared = false;
                    prepareError = "抽音轨失败：解释器或 PyAV 不可用，或容器格式不支持";
                }
                else
                {
                    audioPath = tempWav;
                    lock (_gate)
                    {
                        _job.Message = "第 " + (i + 1) + "/" + files.Count + " 条：" + name + "（转写中…）";
                    }
                }
            }

            if (!prepared)
            {
                lock (_gate)
                {
                    if (i < _job.Items.Count)
                    {
                        _job.Items[i].State = "error";
                        _job.Items[i].Message = prepareError;
                    }

                    _job.Failed = _job.Failed + 1;
                }

                continue;
            }

            int maxTokens = NFor(duration, cfg.Ctx, cfg.MaxTokens);
            lock (_gate)
            {
                _job.Message = "第 " + (i + 1) + "/" + files.Count + " 条：" + name + "（转写中…）";
            }

            string tag = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + i.ToString("000");
            OmniRunResult result = await _engine.RunAsync(new OmniRunRequest
            {
                CliPath = cfg.CliPath,
                ModelPath = cfg.ModelPath,
                MmprojPath = cfg.MmprojPath,
                AudioPath = audioPath,
                Prompt = cfg.Prompt,
                Ctx = cfg.Ctx,
                MaxTokens = maxTokens,
                Ngl = cfg.Ngl,
                Ncmoe = cfg.Ncmoe,
                LogDir = _logDir,
                Tag = tag,
            }, CancellationToken.None).ConfigureAwait(false);

            // [段3] 临时 wav 用完即删（失败也不留——原始素材不受影响）
            if (tempWav.Length > 0)
            {
                try
                {
                    File.Delete(tempWav);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Console.Error.WriteLine("删临时音轨失败：" + tempWav + " · " + ex.Message);
                }
            }

            string textFile = "";
            if (result.Ok && cfg.SaveText)
            {
                textFile = Path.GetFileNameWithoutExtension(file) + ".txt";
                try
                {
                    File.WriteAllText(Path.Combine(_outDir, textFile), result.Text, System.Text.Encoding.UTF8);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    textFile = "";
                    result.Message = result.Message + " · 落 txt 失败：" + ex.Message;
                }
            }

            lock (_gate)
            {
                if (i < _job.Items.Count)
                {
                    OmniJobItem item = _job.Items[i];
                    item.State = result.Ok ? "done" : "error";
                    item.File = textFile;
                    item.Chars = result.Ok ? result.Text.Length : 0;
                    item.AudioSeconds = Math.Round(duration, 1);
                    item.ElapsedSeconds = Math.Round((DateTime.Now - itemStarted).TotalSeconds, 1);
                    item.Message = (video ? "视频抽音轨 · " : "") + result.Message;
                }

                if (result.Ok)
                {
                    _job.Done = _job.Done + 1;
                    _job.GenTokens = result.GenTokens;
                    if (files.Count == 1)
                    {
                        _job.Text = result.Text;
                    }
                }
                else
                {
                    _job.Failed = _job.Failed + 1;
                }
            }
        }

        lock (_gate)
        {
            _job.State = _job.Failed == 0 ? "done" : (_job.Done > 0 ? "done" : "error");
            _job.Message = "完成 " + _job.Done + " 条"
                + (_job.Failed > 0 ? "，失败 " + _job.Failed + " 条" : "")
                + (files.Count == 1 && _job.Text.Length > 0 ? " · " + _job.Text.Length + " 字" : "");
            _job.ElapsedSeconds = Math.Round((DateTime.Now - started).TotalSeconds, 1);
            _job.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }

    /// <summary>生成上限——按输出上界给足，再受上下文预算封顶（与本地链同口径）。</summary>
    /// <param name="duration">音频秒数（0 = 探不到）。</param>
    /// <param name="ctx">上下文长度。</param>
    /// <param name="fixedTokens">面板显式指定的上限（>0 优先）。</param>
    /// <returns>生成上限。</returns>
    private static int NFor(double duration, int ctx, int fixedTokens)
    {
        if (fixedTokens > 0)
        {
            return fixedTokens;
        }

        if (duration <= 0)
        {
            // 时长探不到——给一个够用又不至于跑满上下文的固定上限
            return 16384;
        }

        int want = (int)(duration * 6.5) + 512;
        int budget = ctx - (int)(duration * 13.05) - 200 - 256;
        int value = Math.Min(want, budget);
        return Math.Max(512, value);
    }

    /// <summary>任务状态深拷贝（列表逐条复制）。</summary>
    /// <param name="source">源状态。</param>
    /// <returns>副本。</returns>
    private static OmniJobStatus CloneJob(OmniJobStatus source)
    {
        var copy = new OmniJobStatus
        {
            Id = source.Id,
            State = source.State,
            Message = source.Message,
            InputPreview = source.InputPreview,
            Text = source.Text,
            Total = source.Total,
            Done = source.Done,
            Failed = source.Failed,
            GenTokens = source.GenTokens,
            ElapsedSeconds = source.ElapsedSeconds,
            SubmittedAt = source.SubmittedAt,
            FinishedAt = source.FinishedAt,
        };

        foreach (OmniJobItem item in source.Items)
        {
            copy.Items.Add(new OmniJobItem
            {
                Name = item.Name,
                State = item.State,
                File = item.File,
                Chars = item.Chars,
                AudioSeconds = item.AudioSeconds,
                ElapsedSeconds = item.ElapsedSeconds,
                Message = item.Message,
            });
        }

        return copy;
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

    /// <summary>读配置（没有或坏了给默认值）。</summary>
    /// <returns>配置。</returns>
    private OmniConfig LoadConfig()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                return new OmniConfig();
            }

            string json = File.ReadAllText(_configPath);
            OmniConfig? loaded = JsonSerializer.Deserialize<OmniConfig>(json, JsonOptions());
            return loaded ?? new OmniConfig();
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
        {
            Console.Error.WriteLine("读 omni-config.json 失败：" + ex.Message);
            return new OmniConfig();
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
            Console.Error.WriteLine("写 omni-config.json 失败：" + ex.Message);
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
