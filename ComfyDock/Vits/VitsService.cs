using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using ComfyDock.Rvc;

namespace ComfyDock.Vits;

/// <summary>
/// 本地生成（VITS · MoeGoe 血统）的持久化配置——合成参数 + 换音色参数 + 这一路要用的路径
/// （落盘 <c>data\vits-config.json</c>）。
/// </summary>
public sealed class VitsConfig
{
    /// <summary>解释器（默认 ComfyUI 的 python_embeded）。</summary>
    public string PythonPath { get; set; } = "";

    /// <summary>worker 脚本（随包 runtime\vits\vits_worker.py）。</summary>
    public string ScriptPath { get; set; } = "";

    /// <summary>模型根目录（ComfyUI\models\VITS）。</summary>
    public string ModelsRoot { get; set; } = "";

    /// <summary>推理设备（auto / cuda / cpu）。</summary>
    public string Device { get; set; } = "auto";

    /// <summary>当前选中的模型名。</summary>
    public string Model { get; set; } = "";

    /// <summary>语音合成的文本。</summary>
    public string Text { get; set; } = "こんにちは、世界。";

    /// <summary>说话人索引（合成用）。</summary>
    public int Sid { get; set; }

    /// <summary>语速（length_scale，越大越慢）。</summary>
    public double LengthScale { get; set; } = 1.0;

    /// <summary>音色噪声（noise_scale）。</summary>
    public double NoiseScale { get; set; } = 0.667;

    /// <summary>时长噪声（noise_scale_w）。</summary>
    public double NoiseScaleW { get; set; } = 0.8;

    /// <summary>文本已是音素串（符号输入）——跳过后端 cleaners。</summary>
    public bool Cleaned { get; set; }

    /// <summary>语音转换的输入音频。</summary>
    public string VcAudio { get; set; } = "";

    /// <summary>语音转换的原说话人索引。</summary>
    public int VcSrc { get; set; }

    /// <summary>语音转换的目标说话人索引。</summary>
    public int VcDst { get; set; } = 1;
}

/// <summary>
/// 一个 VITS 模型的「说话人自然基频名册」——逐个说话人合成一句探针文本、量中位 F0。
/// <para>桥接的自动挑源说话人 / 自动变调都读它；落盘 <c>data\vits-f0-profile.json</c>。</para>
/// </summary>
public sealed class VitsF0Profile
{
    /// <summary>模型名。</summary>
    public string Model { get; set; } = "";

    /// <summary>权重文件的修改时刻（ticks）——变了就要重量。</summary>
    public long PthTicks { get; set; }

    /// <summary>权重文件字节数——与 ticks 一起构成「这份名册还新鲜吗」的判据。</summary>
    public long PthBytes { get; set; }

    /// <summary>量的时候用的探针文本（换了文本结果会差一点，记下来便于对账）。</summary>
    public string ProbeText { get; set; } = "";

    /// <summary>说话人索引（十进制字符串）→ 中位 F0（Hz）——键用字符串，落盘口径不挑序列化器。</summary>
    public Dictionary<string, double> F0 { get; set; } = new Dictionary<string, double>();

    /// <summary>取某个说话人的 F0（没量到给 0）。</summary>
    /// <param name="sid">说话人索引。</param>
    /// <returns>中位 F0（Hz）；缺失 0。</returns>
    public double F0Of(int sid)
    {
        return F0.TryGetValue(sid.ToString(), out double v) ? v : 0;
    }

    /// <summary>量好/更新时间。</summary>
    public string At { get; set; } = "";

    /// <summary>成功条数。</summary>
    public int Count { get; set; }
}

/// <summary>一个 VITS 模型（面板下拉用）——.pth 与同名 .json 成对才算可用。</summary>
public sealed class VitsModelItem
{
    /// <summary>分组（模型根下的一层子目录）。</summary>
    public string Group { get; set; } = "";

    /// <summary>模型名（文件名去后缀）。</summary>
    public string Name { get; set; } = "";

    /// <summary>权重路径。</summary>
    public string Pth { get; set; } = "";

    /// <summary>config 路径（空 = 缺）。</summary>
    public string Config { get; set; } = "";

    /// <summary>说话人数。</summary>
    public int Speakers { get; set; }

    /// <summary>原生采样率。</summary>
    public int SamplingRate { get; set; }

    /// <summary>是否可用（config 在位 + 解析成功）。</summary>
    public bool Ok { get; set; }

    /// <summary>权重体积（MB）。</summary>
    public double SizeMb { get; set; }
}

/// <summary>一个说话人（面板下拉用）。</summary>
public sealed class VitsSpeakerItem
{
    /// <summary>索引。</summary>
    public int Index { get; set; }

    /// <summary>名字（模型 config 里的原文）。</summary>
    public string Name { get; set; } = "";
}

/// <summary>
/// 一次合成 / 换音色的任务状态（面板轮询它）。
/// </summary>
public sealed class VitsJobItem
{
    /// <summary>任务 id。</summary>
    public string Id { get; set; } = "";

    /// <summary>种类：tts / vc。</summary>
    public string Kind { get; set; } = "";

    /// <summary>状态：idle / running / done / error。</summary>
    public string Status { get; set; } = "idle";

    /// <summary>产物文件名。</summary>
    public string File { get; set; } = "";

    /// <summary>音频秒数。</summary>
    public double AudioSeconds { get; set; }

    /// <summary>喂进模型的 token 数（合成链）。</summary>
    public int Tokens { get; set; }

    /// <summary>耗时毫秒。</summary>
    public long Ms { get; set; }

    /// <summary>说明 / 失败原因。</summary>
    public string Message { get; set; } = "";

    /// <summary>完成时刻。</summary>
    public string At { get; set; } = "";

    /// <summary>批量已完成条数（单条任务为 0）。</summary>
    public int Progress { get; set; }

    /// <summary>批量总条数（单条任务为 0）。</summary>
    public int Total { get; set; }

    /// <summary>批量已完成的条目（边跑边追加——面板按它逐条即时填格）。</summary>
    public List<VitsBatchCell> Cells { get; set; } = new List<VitsBatchCell>();
}

/// <summary>批量合成的一格里那条产物（面板按它渲染矩阵）。</summary>
public sealed class VitsBatchCell
{
    /// <summary>说话人索引。</summary>
    public int Sid { get; set; }

    /// <summary>说话人名字（模型名册里的原文）。</summary>
    public string Name { get; set; } = "";

    /// <summary>产物文件名（空 = 没出产物）。</summary>
    public string File { get; set; } = "";

    /// <summary>音频秒数。</summary>
    public double Seconds { get; set; }

    /// <summary>这条耗时毫秒。</summary>
    public long Ms { get; set; }

    /// <summary>失败原因（成功为空）。</summary>
    public string Error { get; set; } = "";
}

/// <summary>一次批量合成 = 产物矩阵里的一列（同一条文本、同一批勾选的说话人）。</summary>
public sealed class VitsBatchColumn
{
    /// <summary>批次 id（也是文件名时间戳前缀）。</summary>
    public string Id { get; set; } = "";

    /// <summary>跑的模型名。</summary>
    public string Model { get; set; } = "";

    /// <summary>这一批用的文本（矩阵的「发言内容」）。</summary>
    public string Text { get; set; } = "";

    /// <summary>批次完成时刻。</summary>
    public string At { get; set; } = "";

    /// <summary>逐条产物。</summary>
    public List<VitsBatchCell> Cells { get; set; } = new List<VitsBatchCell>();
}

/// <summary>
/// 本地生成板块（VITS · MoeGoe 血统）的服务面——模型清单 + 配置 + 常驻引擎投递 + 产物清单。
/// <para>与 IrodoriTTS / RVC 同一形态：不启 ComfyUI 服务、不开端口，解释器 = ComfyUI 的 python_embeded。</para>
/// </summary>
public sealed class VitsService
{
    /// <summary>配置落盘路径。</summary>
    private readonly string _configPath;

    /// <summary>产物落点目录。</summary>
    private readonly string _outDir;

    /// <summary>数据目录（F0 名册与配置都落这里）。</summary>
    private readonly string _dataDir;

    /// <summary>F0 名册缓存文件路径。</summary>
    private readonly string _f0ProfilePath;

    /// <summary>F0 名册后台测量状态：idle / running / ready / error。</summary>
    private string _f0State = "idle";

    /// <summary>F0 名册状态说明（面板照它出声）。</summary>
    private string _f0Message = "";

    /// <summary>F0 名册已量条数。</summary>
    private int _f0Done;

    /// <summary>F0 名册总条数。</summary>
    private int _f0Total;

    /// <summary>正在量哪个模型的名册（空 = 没在跑）。</summary>
    private string _f0Running = "";

    /// <summary>F0 名册缓存（模型名 → 名册）。</summary>
    private Dictionary<string, VitsF0Profile> _f0Cache = new Dictionary<string, VitsF0Profile>();

    /// <summary>F0 名册测量期间用的探针文本（够多浊音帧，又不至于太慢）。</summary>
    public const string F0ProbeText = "こんにちは、世界。今日はいい天気ですね。";

    /// <summary>状态锁。</summary>
    private readonly object _gate = new object();

    /// <summary>常驻引擎。</summary>
    private readonly VitsEngine _engine = new VitsEngine();

    /// <summary>当前配置。</summary>
    private VitsConfig _config;

    /// <summary>当前任务状态。</summary>
    private VitsJobItem _job = new VitsJobItem();

    /// <summary>以选项构造。</summary>
    /// <param name="defaults">默认配置。</param>
    /// <param name="dataDir">数据目录。</param>
    /// <param name="outDir">产物目录（空 = 数据目录同级 out\vits）。</param>
    public VitsService(VitsConfig defaults, string dataDir, string outDir)
    {
        _dataDir = dataDir;
        _configPath = Path.Combine(dataDir, "vits-config.json");
        _f0ProfilePath = Path.Combine(dataDir, "vits-f0-profile.json");
        _outDir = string.IsNullOrWhiteSpace(outDir)
            ? Path.Combine(AppContext.BaseDirectory, "out", "vits")
            : outDir;
        _config = LoadConfig(defaults);
        LoadF0Cache();
        ApplyEnginePaths();
    }

    /// <summary>产物落点目录。</summary>
    public string OutDir => _outDir;

    /// <summary>常驻引擎状态（面板显示这一路的死活）。</summary>
    public VitsEngineState Engine => _engine.State;

    /// <summary>当前任务状态（拷贝，防外部改）。</summary>
    /// <returns>任务快照。</returns>
    public VitsJobItem Job()
    {
        lock (_gate)
        {
            return new VitsJobItem
            {
                Id = _job.Id,
                Kind = _job.Kind,
                Status = _job.Status,
                File = _job.File,
                AudioSeconds = _job.AudioSeconds,
                Tokens = _job.Tokens,
                Ms = _job.Ms,
                Message = _job.Message,
                At = _job.At,
                Progress = _job.Progress,
                Total = _job.Total,
                Cells = new List<VitsBatchCell>(_job.Cells),
            };
        }
    }
    /// <summary>F0 名册缓存文件路径（桥接侧按它读名册）。</summary>
    public string F0ProfilePath => _f0ProfilePath;

    /// <summary>查某个说话人的自然 F0（名册里没有给 0）。</summary>
    /// <param name="model">模型名。</param>
    /// <param name="sid">说话人索引。</param>
    /// <returns>中位 F0（Hz）；缺失 0。</returns>
    public double F0Of(string model, int sid)
    {
        lock (_gate)
        {
            return F0ProfileOfLocked(model)?.F0Of(sid) ?? 0;
        }
    }

    /// <summary>桥接要用的 VITS 运行面——路径 / 设备 / 名册 + F0 查询（RVC 页签的桥接按它组装 spec）。</summary>
    /// <returns>运行面快照。</returns>
    public RvcVitsRuntime BridgeRuntime()
    {
        lock (_gate)
        {
            string script = _config.ScriptPath;
            string repo = script.Length > 0 ? Path.GetDirectoryName(script) ?? "" : "";
            if (repo.Length == 0)
            {
                repo = Path.Combine(AppContext.BaseDirectory, "runtime", "vits");
            }

            return new RvcVitsRuntime
            {
                ModelsRoot = _config.ModelsRoot,
                Repo = repo,
                Device = _config.Device.Length == 0 ? "cuda" : _config.Device,
                F0Profile = _f0ProfilePath,
                F0Of = F0Of,
            };
        }
    }

    /// <summary>桥接下拉用的模型清单（只回可用项，带说话人数与采样率）。</summary>
    /// <returns>模型条目。</returns>
    public List<RvcVitsModelItem> BridgeModels()
    {
        var list = new List<RvcVitsModelItem>();
        foreach (VitsModelItem m in ListModels())
        {
            list.Add(new RvcVitsModelItem
            {
                Name = m.Name,
                Group = m.Group,
                Speakers = m.Speakers,
                SamplingRate = m.SamplingRate,
                Ok = m.Ok,
            });
        }

        return list;
    }

    /// <summary>桥接下拉用的说话人清单（索引 / 名字 / 自然 F0）。</summary>
    /// <param name="model">模型名。</param>
    /// <returns>说话人条目。</returns>
    public List<RvcVitsSpeakerItem> BridgeSpeakers(string model)
    {
        var list = new List<RvcVitsSpeakerItem>();
        if (string.IsNullOrWhiteSpace(model))
        {
            return list;
        }

        VitsF0Profile? prof = F0ProfileOf(model);
        foreach (VitsSpeakerItem s in SpeakersOf(model))
        {
            list.Add(new RvcVitsSpeakerItem
            {
                Index = s.Index,
                Name = s.Name,
                F0 = prof?.F0Of(s.Index) ?? 0,
            });
        }

        return list;
    }

    /// <summary>读一份名册（模型名 → 名册；没有或权重已换则 null）。</summary>
    /// <param name="model">模型名。</param>
    /// <returns>名册或 null。</returns>
    public VitsF0Profile? F0ProfileOf(string model)
    {
        lock (_gate)
        {
            return F0ProfileOfLocked(model);
        }
    }

    /// <summary>F0 名册的当前状态（面板显示 + 桥接侧出声用）。</summary>
    /// <returns>状态快照。</returns>
    public object F0State()
    {
        lock (_gate)
        {
            VitsF0Profile? prof = F0ProfileOfLocked(_config.Model);
            return new
            {
                state = _f0State,
                message = _f0Message,
                running = _f0Running,
                done = _f0Done,
                total = _f0Total,
                model = _config.Model,
                count = prof?.Count ?? 0,
                at = prof?.At ?? "",
                probeText = prof?.ProbeText ?? "",
                path = _f0ProfilePath,
            };
        }
    }

    /// <summary>
    /// 确保当前模型的 F0 名册在位——没有、或权重换过了就量一遍（逐条回调更新进度）。
    /// <para>缺名册时桥接会退回「按 VITS 页签手动选的源说话人」，并在状态里出声，不静默。</para>
    /// </summary>
    /// <param name="model">模型名（空 = 用当前配置的模型）。</param>
    /// <param name="force">为 true 时无条件重量。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否就绪, 人话)。</returns>
    public async Task<(bool Ok, string Message)> EnsureF0ProfileAsync(string model, bool force, CancellationToken ct)
    {
        string name = string.IsNullOrWhiteSpace(model) ? _config.Model : model.Trim();
        if (name.Length == 0)
        {
            return (false, "还没选模型——先去模型下拉里选一个");
        }

        VitsModelItem? item = ListModels().FirstOrDefault(m => m.Name == name);
        if (item is null || !item.Ok)
        {
            return (false, "模型不在位或缺 config：" + name);
        }

        long ticks = 0;
        long bytes = 0;
        try
        {
            var fi = new FileInfo(item.Pth);
            ticks = fi.LastWriteTimeUtc.Ticks;
            bytes = fi.Length;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return (false, "读不到权重文件：" + ex.Message);
        }

        lock (_gate)
        {
            if (_f0Running.Length > 0)
            {
                return (false, "已经在量名册了：" + _f0Running);
            }

            VitsF0Profile? have = F0ProfileOfLocked(name);
            bool fresh = have is not null && have.PthTicks == ticks && have.PthBytes == bytes
                && have.Count > 0 && have.ProbeText == F0ProbeText;
            if (fresh && !force)
            {
                _f0State = "ready";
                _f0Message = "F0 名册在位：" + have!.Count + " 人（" + have.At + "）";
                return (true, _f0Message);
            }

            _f0Running = name;
            _f0State = "running";
            _f0Done = 0;
            _f0Total = Math.Max(1, item.Speakers);
            _f0Message = "正在量 F0 名册（" + _f0Total + " 人）…";
        }

        (bool loaded, string loadMessage, _) = await _engine.LoadAsync(name, ct).ConfigureAwait(false);
        if (!loaded)
        {
            lock (_gate)
            {
                _f0Running = "";
                _f0State = "error";
                _f0Message = "名册要靠引擎跑，但加载模型失败：" + loadMessage;
            }

            return (false, _f0Message);
        }

        (Dictionary<int, double> got, string error) = await _engine
            .F0ProfileAsync(F0ProbeText, null, (sid, f0, done, total) =>
            {
                lock (_gate)
                {
                    _f0Done = done;
                    if (total > 0)
                    {
                        _f0Total = total;
                    }

                    _f0Message = "正在量 F0 名册… " + _f0Done + "/" + _f0Total;
                }
            }, ct).ConfigureAwait(false);

        lock (_gate)
        {
            _f0Running = "";
            if (error.Length > 0 || got.Count == 0)
            {
                _f0State = "error";
                _f0Message = "F0 名册没量成：" + (error.Length > 0 ? error : "一条都没测出来");
                return (false, _f0Message);
            }

            var prof = new VitsF0Profile
            {
                Model = name,
                PthTicks = ticks,
                PthBytes = bytes,
                ProbeText = F0ProbeText,
                At = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Count = got.Count,
            };
            foreach (KeyValuePair<int, double> kv in got)
            {
                prof.F0[kv.Key.ToString()] = Math.Round(kv.Value, 1);
            }

            _f0Cache[name] = prof;
            SaveF0CacheLocked();
            _f0State = "ready";
            _f0Message = "F0 名册已就绪：" + prof.Count + " 人";
            return (true, _f0Message);
        }
    }

    /// <summary>读名册缓存（锁内调用）。</summary>
    /// <param name="model">模型名。</param>
    /// <returns>名册或 null。</returns>
    private VitsF0Profile? F0ProfileOfLocked(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        return _f0Cache.TryGetValue(model, out VitsF0Profile? prof) ? prof : null;
    }

    /// <summary>把名册缓存读进内存（启动时一次）。</summary>
    private void LoadF0Cache()
    {
        _f0Cache = new Dictionary<string, VitsF0Profile>();
        if (!File.Exists(_f0ProfilePath))
        {
            return;
        }

        try
        {
            string text = File.ReadAllText(_f0ProfilePath, Encoding.UTF8);
            Dictionary<string, VitsF0Profile>? loaded =
                JsonSerializer.Deserialize<Dictionary<string, VitsF0Profile>>(text);
            if (loaded is not null)
            {
                _f0Cache = loaded;
            }
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
        {
            _f0State = "error";
            _f0Message = "F0 名册缓存读不出来（会重量一份）：" + ex.Message;
        }
    }

    /// <summary>把名册缓存写回磁盘（锁内调用）。</summary>
    private void SaveF0CacheLocked()
    {
        try
        {
            string json = JsonSerializer.Serialize(_f0Cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_f0ProfilePath, json, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _f0State = "error";
            _f0Message = "F0 名册写不进磁盘：" + ex.Message;
        }
    }

    /// <summary>面板快照——配置 + 引擎 + 模型清单 + 当前模型说话人 + 产物。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>快照对象。</returns>
    public async Task<object> SnapshotAsync(CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        VitsConfig cfg;
        lock (_gate)
        {
            cfg = CloneConfig(_config);
        }

        List<VitsModelItem> models = ListModels();
        List<VitsSpeakerItem> speakers = cfg.Model.Length > 0 ? SpeakersOf(cfg.Model) : new List<VitsSpeakerItem>();
        List<string> outputs = ListOutputs();

        return new
        {
            config = cfg,
            engine = _engine.State,
            job = Job(),
            models,
            speakers,
            outputs,
            f0 = F0State(),
            outDir = _outDir,
            scriptOk = cfg.ScriptPath.Length > 0 && File.Exists(cfg.ScriptPath),
            pythonOk = cfg.PythonPath.Length > 0 && File.Exists(cfg.PythonPath),
            modelsRootOk = cfg.ModelsRoot.Length > 0 && Directory.Exists(cfg.ModelsRoot),
        };
    }

    /// <summary>改配置（传进来的非空字符串 / 非零数值覆盖，其余不动）。</summary>
    /// <param name="next">新配置。</param>
    /// <returns>落盘后的配置。</returns>
    public VitsConfig UpdateConfig(VitsConfig next)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(next.PythonPath))
            {
                _config.PythonPath = next.PythonPath.Trim();
            }

            if (!string.IsNullOrWhiteSpace(next.ScriptPath))
            {
                _config.ScriptPath = next.ScriptPath.Trim();
            }

            if (!string.IsNullOrWhiteSpace(next.ModelsRoot))
            {
                _config.ModelsRoot = next.ModelsRoot.Trim();
            }

            if (!string.IsNullOrWhiteSpace(next.Device))
            {
                _config.Device = next.Device.Trim();
            }

            if (!string.IsNullOrWhiteSpace(next.Model))
            {
                _config.Model = next.Model.Trim();
            }

            if (next.Text is not null)
            {
                _config.Text = next.Text;
            }

            if (next.VcAudio is not null)
            {
                _config.VcAudio = next.VcAudio.Trim();
            }

            _config.Sid = Math.Max(0, next.Sid);
            _config.VcSrc = Math.Max(0, next.VcSrc);
            _config.VcDst = Math.Max(0, next.VcDst);
            _config.LengthScale = Clamp(next.LengthScale <= 0 ? 1.0 : next.LengthScale, 0.2, 3.0);
            _config.NoiseScale = Clamp(next.NoiseScale <= 0 ? 0.667 : next.NoiseScale, 0, 2.0);
            _config.NoiseScaleW = Clamp(next.NoiseScaleW <= 0 ? 0.8 : next.NoiseScaleW, 0, 2.0);
            _config.Cleaned = next.Cleaned;

            SaveConfig();
            ApplyEnginePaths();
            return CloneConfig(_config);
        }
    }

    /// <summary>加载模型（切模型时旧模型被换掉）。</summary>
    /// <param name="model">模型名（空 = 用配置里那个）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 说明)。</returns>
    public async Task<(bool Ok, string Message)> LoadAsync(string model, CancellationToken ct)
    {
        string name = model;
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                name = _config.Model;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return (false, "还没选模型");
            }

            _config.Model = name;
            SaveConfig();
            ApplyEnginePaths();
        }

        (bool ok, string message, JsonNode? node) = await _engine.LoadAsync(name, ct).ConfigureAwait(false);
        if (ok && node is not null)
        {
            int count = (int)(node["n_speakers"]?.GetValue<double>() ?? 0);
            lock (_gate)
            {
                if (_config.Sid >= count)
                {
                    _config.Sid = 0;
                }

                if (_config.VcDst >= count)
                {
                    _config.VcDst = count > 1 ? 1 : 0;
                }

                if (_config.VcSrc >= count)
                {
                    _config.VcSrc = 0;
                }
            }
        }

        return (ok, message);
    }

    /// <summary>清理预览（文本 → 音素串）。</summary>
    /// <param name="text">文本（空 = 用配置里那段）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(清理结果, 错误说明)。</returns>
    public async Task<(string Cleaned, string Error)> CleanAsync(string text, CancellationToken ct)
    {
        string model;
        string body;
        lock (_gate)
        {
            model = _config.Model;
            body = string.IsNullOrEmpty(text) ? _config.Text : text;
        }

        if (model.Length == 0)
        {
            return ("", "还没选模型");
        }

        (bool ok, string message) = await LoadAsync(model, ct).ConfigureAwait(false);
        if (!ok)
        {
            return ("", message);
        }

        return await _engine.CleanAsync(body, ct).ConfigureAwait(false);
    }

    /// <summary>按当前配置合成一句（前台等回执——单句通常 1~3 秒）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 说明)。</returns>
    public async Task<(bool Ok, string Message)> TtsAsync(CancellationToken ct)
    {
        VitsConfig cfg;
        lock (_gate)
        {
            cfg = CloneConfig(_config);
        }

        if (cfg.Model.Length == 0)
        {
            return (false, "还没选模型");
        }

        if (cfg.Cleaned && cfg.Text.Trim().Length == 0)
        {
            return (false, "符号输入为空——打开符号输入时要自己填音素串");
        }

        (bool loaded, string loadMessage) = await LoadAsync(cfg.Model, ct).ConfigureAwait(false);
        if (!loaded)
        {
            return (false, loadMessage);
        }

        string name = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Sanitize(cfg.Model) + "_tts.wav";
        string path = Path.Combine(_outDir, name);
        Directory.CreateDirectory(_outDir);

        SetJob("tts", "running", "", "合成中…");
        long t0 = Environment.TickCount64;
        (bool ok, string file, double seconds, int tokens, string message) = await _engine
            .TtsAsync(cfg.Text, cfg.Sid, cfg.LengthScale, cfg.NoiseScale, cfg.NoiseScaleW, cfg.Cleaned, path, ct)
            .ConfigureAwait(false);
        long ms = Environment.TickCount64 - t0;

        if (!ok)
        {
            SetJob("tts", "error", "", message);
            return (false, message);
        }

        SetJob("tts", "done", Path.GetFileName(file), message, seconds, tokens, ms);
        return (true, message + "（" + (ms / 1000.0).ToString("F1") + " 秒）");
    }

    /// <summary>同一条文本批量跑勾选的说话人——一次请求、一次模型加载、一次文本前端。</summary>
    /// <param name="sids">勾选的说话人索引（越界与重复在这里收口）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 说明, 本批结果——面板按它新增一列)。</returns>
    public async Task<(bool Ok, string Message, VitsBatchColumn? Batch)> TtsBatchAsync(
        IReadOnlyList<int> sids,
        CancellationToken ct)
    {
        VitsConfig cfg;
        lock (_gate)
        {
            cfg = CloneConfig(_config);
        }

        if (cfg.Model.Length == 0)
        {
            return (false, "还没选模型", null);
        }

        if (cfg.Text.Trim().Length == 0)
        {
            return (false, "文本为空——打开符号输入时要自己填音素串", null);
        }

        List<VitsSpeakerItem> roster = SpeakersOf(cfg.Model);
        var picked = new List<int>();
        foreach (int sid in sids)
        {
            if (sid < 0)
            {
                continue;
            }

            if (roster.Count > 0 && sid >= roster.Count)
            {
                continue;
            }

            if (!picked.Contains(sid))
            {
                picked.Add(sid);
            }
        }

        if (picked.Count == 0)
        {
            return (false, "没有勾选任何说话人", null);
        }

        (bool loaded, string loadMessage) = await LoadAsync(cfg.Model, ct).ConfigureAwait(false);
        if (!loaded)
        {
            return (false, loadMessage, null);
        }

        string batchId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        Directory.CreateDirectory(_outDir);
        var outs = new List<string>();
        foreach (int sid in picked)
        {
            outs.Add(Path.Combine(_outDir, batchId + "_" + Sanitize(cfg.Model) + "_s" + sid + "_tts.wav"));
        }

        SetJob("tts-batch", "running", "", "批量合成中… 0/" + picked.Count, 0, 0, 0, 0, picked.Count);
        long t0 = Environment.TickCount64;
        (bool ok, string message, List<VitsBatchItem> items) = await _engine
            .TtsBatchAsync(cfg.Text, picked, outs, cfg.LengthScale, cfg.NoiseScale, cfg.NoiseScaleW, cfg.Cleaned,
                (item, done, total) =>
                {
                    // 每出一条就落进 job.Cells——/api/vits/live 轮询能立刻拿到，面板边跑边填格
                    lock (_gate)
                    {
                        _job.Cells.Add(new VitsBatchCell
                        {
                            Sid = item.Sid,
                            Name = item.Sid >= 0 && item.Sid < roster.Count ? roster[item.Sid].Name : ("spk" + item.Sid),
                            File = item.Out.Length > 0 ? Path.GetFileName(item.Out) : "",
                            Seconds = item.Seconds,
                            Ms = item.Ms,
                            Error = item.Error,
                        });
                        _job.Progress = done;
                        _job.Total = total;
                        _job.Message = "批量合成中… " + done + "/" + total;
                    }
                },
                ct)
            .ConfigureAwait(false);
        long ms = Environment.TickCount64 - t0;

        var column = new VitsBatchColumn
        {
            Id = batchId,
            Model = cfg.Model,
            Text = cfg.Text,
            At = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        foreach (VitsBatchItem item in items)
        {
            int sid = item.Sid;
            column.Cells.Add(new VitsBatchCell
            {
                Sid = sid,
                Name = sid >= 0 && sid < roster.Count ? roster[sid].Name : ("spk" + sid),
                File = item.Out.Length > 0 ? Path.GetFileName(item.Out) : "",
                Seconds = item.Seconds,
                Ms = item.Ms,
                Error = item.Error,
            });
        }

        if (!ok)
        {
            SetJob("tts-batch", "error", "", message, 0, 0, ms, column.Cells.Count, picked.Count, column.Cells);
            return (false, message, column);
        }

        int doneCount = 0;
        double seconds = 0;
        foreach (VitsBatchCell cell in column.Cells)
        {
            if (cell.Error.Length == 0)
            {
                doneCount++;
                seconds += cell.Seconds;
            }
        }

        string summary = "批次完成 " + doneCount + "/" + picked.Count + "（" + (ms / 1000.0).ToString("F1") + " 秒）";
        SetJob("tts-batch", "done", "", summary, seconds, 0, ms, picked.Count, picked.Count, column.Cells);
        return (true, summary, column);
    }

    /// <summary>按当前配置做一次语音转换（原说话人 → 目标说话人）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(是否成功, 说明)。</returns>
    public async Task<(bool Ok, string Message)> VcAsync(CancellationToken ct)
    {
        VitsConfig cfg;
        lock (_gate)
        {
            cfg = CloneConfig(_config);
        }

        if (cfg.Model.Length == 0)
        {
            return (false, "还没选模型");
        }

        if (cfg.VcAudio.Length == 0 || !File.Exists(cfg.VcAudio))
        {
            return (false, "输入音频不存在：" + cfg.VcAudio);
        }

        (bool loaded, string loadMessage) = await LoadAsync(cfg.Model, ct).ConfigureAwait(false);
        if (!loaded)
        {
            return (false, loadMessage);
        }

        string name = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Sanitize(cfg.Model)
            + "_vc" + cfg.VcSrc + "to" + cfg.VcDst + ".wav";
        string path = Path.Combine(_outDir, name);
        Directory.CreateDirectory(_outDir);

        SetJob("vc", "running", "", "换音色中…");
        long t0 = Environment.TickCount64;
        (bool ok, string file, double seconds, int tokens, string message) = await _engine
            .VcAsync(cfg.VcAudio, cfg.VcSrc, cfg.VcDst, path, ct)
            .ConfigureAwait(false);
        long ms = Environment.TickCount64 - t0;

        if (!ok)
        {
            SetJob("vc", "error", "", message);
            return (false, message);
        }

        SetJob("vc", "done", Path.GetFileName(file), message, seconds, 0, ms);
        return (true, message + "（" + (ms / 1000.0).ToString("F1") + " 秒）");
    }

    /// <summary>停掉常驻引擎（把显存还回去）。</summary>
    /// <returns>说明。</returns>
    public string Stop()
    {
        _engine.Stop();
        return "引擎已停止";
    }

    /// <summary>产物目录里的音频清单（新→旧）。</summary>
    /// <returns>文件名清单。</returns>
    public List<string> ListOutputs()
    {
        var list = new List<string>();
        if (!Directory.Exists(_outDir))
        {
            return list;
        }

        try
        {
            var info = new DirectoryInfo(_outDir);
            foreach (FileInfo file in info.GetFiles("*.wav").OrderByDescending(f => f.LastWriteTimeUtc).Take(20))
            {
                list.Add(file.Name);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return list;
        }

        return list;
    }

    /// <summary>产物音频的完整路径（面板播放用）。</summary>
    /// <param name="name">文件名。</param>
    /// <returns>完整路径（空 = 非法名 / 不存在）。</returns>
    public string OutputPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '/', '\\' }) >= 0)
        {
            return "";
        }

        string path = Path.Combine(_outDir, name);
        return File.Exists(path) ? path : "";
    }

    /// <summary>模型清单（模型根下一层分组）。</summary>
    /// <returns>模型条目。</returns>
    public List<VitsModelItem> ListModels()
    {
        var list = new List<VitsModelItem>();
        string root;
        lock (_gate)
        {
            root = _config.ModelsRoot;
        }

        if (root.Length == 0 || !Directory.Exists(root))
        {
            return list;
        }

        try
        {
            foreach (string groupDir in Directory.GetDirectories(root))
            {
                string group = Path.GetFileName(groupDir);
                foreach (string pth in Directory.GetFiles(groupDir, "*.pth"))
                {
                    string name = Path.GetFileNameWithoutExtension(pth);
                    string cfg = Path.Combine(groupDir, name + ".json");
                    var item = new VitsModelItem
                    {
                        Group = group,
                        Name = name,
                        Pth = pth,
                        Config = File.Exists(cfg) ? cfg : "",
                        SizeMb = Math.Round(new FileInfo(pth).Length / 1048576.0, 1),
                    };

                    if (item.Config.Length > 0)
                    {
                        (int speakers, int rate, bool ok) = ReadModelMeta(cfg);
                        item.Speakers = speakers;
                        item.SamplingRate = rate;
                        item.Ok = ok;
                    }

                    list.Add(item);
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return list;
        }

        return list;
    }

    /// <summary>某个模型的说话人清单——按 config 的 <c>data.n_speakers</c> 全量列（名册数组只覆盖有名字的那部分，其余回落 spk&lt;索引&gt;）。</summary>
    /// <param name="model">模型名。</param>
    /// <returns>说话人清单。</returns>
    public List<VitsSpeakerItem> SpeakersOf(string model)
    {
        var list = new List<VitsSpeakerItem>();
        VitsModelItem? hit = ListModels().FirstOrDefault(m => m.Name == model && m.Ok);
        if (hit is null || hit.Config.Length == 0)
        {
            return list;
        }

        try
        {
            string text = File.ReadAllText(hit.Config, Encoding.UTF8);
            JsonNode? node = JsonNode.Parse(text);
            JsonArray? arr = node?["speakers"] as JsonArray;
            int named = arr is null ? 0 : arr.Count;
            int total = (int)((node?["data"] as JsonObject)?["n_speakers"]?.GetValue<double>() ?? 0);
            int count = Math.Max(total, named);
            for (int i = 0; i < count; i++)
            {
                string name = "";
                if (i < named)
                {
                    try
                    {
                        name = arr![i]?.GetValue<string>() ?? "";
                    }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is FormatException)
                    {
                        name = "";
                    }
                }

                list.Add(new VitsSpeakerItem
                {
                    Index = i,
                    Name = name.Length > 0 ? name : ("spk" + i),
                });
            }
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
        {
            return list;
        }

        return list;
    }

    /// <summary>把配置里的路径推给常驻引擎（参数没变就什么都不做）。</summary>
    private void ApplyEnginePaths()
    {
        VitsConfig cfg;
        lock (_gate)
        {
            cfg = CloneConfig(_config);
        }

        _engine.Prepare(cfg.PythonPath, cfg.ScriptPath, cfg.ModelsRoot, cfg.Device);
    }

    /// <summary>读模型 config 的元信息。</summary>
    /// <param name="path">config 路径。</param>
    /// <returns>(说话人数, 采样率, 是否解析成功)。</returns>
    private static (int Speakers, int Rate, bool Ok) ReadModelMeta(string path)
    {
        try
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            JsonNode? node = JsonNode.Parse(text);
            JsonObject? data = node?["data"] as JsonObject;
            int speakers = (int)(data?["n_speakers"]?.GetValue<double>() ?? 0);
            int rate = (int)(data?["sampling_rate"]?.GetValue<double>() ?? 0);
            return (speakers, rate, rate > 0);
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
        {
            return (0, 0, false);
        }
    }

    /// <summary>更新任务状态。</summary>
    /// <param name="kind">种类。</param>
    /// <param name="status">状态。</param>
    /// <param name="file">产物。</param>
    /// <param name="message">说明。</param>
    /// <param name="seconds">音频秒数。</param>
    /// <param name="tokens">token 数。</param>
    /// <param name="ms">耗时。</param>
    private void SetJob(string kind, string status, string file, string message,
        double seconds = 0, int tokens = 0, long ms = 0, int progress = 0, int total = 0,
        List<VitsBatchCell>? cells = null)
    {
        lock (_gate)
        {
            _job = new VitsJobItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = kind,
                Status = status,
                File = file,
                AudioSeconds = seconds,
                Tokens = tokens,
                Ms = ms,
                Message = message,
                At = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Progress = progress,
                Total = total,
                Cells = cells is null ? new List<VitsBatchCell>() : new List<VitsBatchCell>(cells),
            };
        }
    }

    /// <summary>文件名里的安全化（去非法字符）。</summary>
    /// <param name="name">原名。</param>
    /// <returns>安全名。</returns>
    private static string Sanitize(string name)
    {
        char[] bad = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
        }

        return sb.ToString();
    }

    /// <summary>读配置（坏文件回落默认值，不阻断启动）。</summary>
    /// <param name="defaults">默认值。</param>
    /// <returns>配置。</returns>
    private VitsConfig LoadConfig(VitsConfig defaults)
    {
        if (!File.Exists(_configPath))
        {
            return defaults;
        }

        try
        {
            string text = File.ReadAllText(_configPath, Encoding.UTF8);
            VitsConfig? loaded = JsonSerializer.Deserialize<VitsConfig>(text, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

            if (loaded is null)
            {
                return defaults;
            }

            // 路径类字段为空时回落默认值（面板从没改过就沿用选项）
            if (string.IsNullOrWhiteSpace(loaded.PythonPath))
            {
                loaded.PythonPath = defaults.PythonPath;
            }

            if (string.IsNullOrWhiteSpace(loaded.ScriptPath))
            {
                loaded.ScriptPath = defaults.ScriptPath;
            }

            if (string.IsNullOrWhiteSpace(loaded.ModelsRoot))
            {
                loaded.ModelsRoot = defaults.ModelsRoot;
            }

            if (string.IsNullOrWhiteSpace(loaded.Device))
            {
                loaded.Device = defaults.Device;
            }

            if (string.IsNullOrWhiteSpace(loaded.Model))
            {
                loaded.Model = defaults.Model;
            }

            return loaded;
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
        {
            return defaults;
        }
    }

    /// <summary>落盘配置（原子替换）。</summary>
    private void SaveConfig()
    {
        try
        {
            string dir = Path.GetDirectoryName(_configPath) ?? ".";
            Directory.CreateDirectory(dir);
            string tmp = _configPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_config, new JsonSerializerOptions
            {
                WriteIndented = true,
            }), new UTF8Encoding(false));
            File.Move(tmp, _configPath, true);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // 配置写不进去不该阻断推理——但要具名出声（失败必须可见）
            Console.Error.WriteLine("[vits] 配置写盘失败：" + ex.Message);
        }
    }

    /// <summary>配置拷贝。</summary>
    /// <param name="src">源。</param>
    /// <returns>拷贝。</returns>
    private static VitsConfig CloneConfig(VitsConfig src)
    {
        return new VitsConfig
        {
            PythonPath = src.PythonPath,
            ScriptPath = src.ScriptPath,
            ModelsRoot = src.ModelsRoot,
            Device = src.Device,
            Model = src.Model,
            Text = src.Text,
            Sid = src.Sid,
            LengthScale = src.LengthScale,
            NoiseScale = src.NoiseScale,
            NoiseScaleW = src.NoiseScaleW,
            Cleaned = src.Cleaned,
            VcAudio = src.VcAudio,
            VcSrc = src.VcSrc,
            VcDst = src.VcDst,
        };
    }

    /// <summary>夹取数值范围。</summary>
    /// <param name="value">值。</param>
    /// <param name="min">下界。</param>
    /// <param name="max">上界。</param>
    /// <returns>夹取结果。</returns>
    private static double Clamp(double value, double min, double max)
    {
        return value < min ? min : (value > max ? max : value);
    }
}

/// <summary>面板发来的纯文本请求（清理预览用）。</summary>
public sealed class VitsTextRequest
{
    /// <summary>要清理的文本。</summary>
    public string? Text { get; set; }
}

/// <summary>面板发来的批量合成请求——勾了哪些说话人就跑哪些。</summary>
public sealed class VitsBatchRequest
{
    /// <summary>勾选的说话人索引（空 = 不跑）。</summary>
    public List<int>? Sids { get; set; }
}
