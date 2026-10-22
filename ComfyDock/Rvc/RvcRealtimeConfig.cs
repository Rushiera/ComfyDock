using System;
using System.Collections.Generic;

namespace ComfyDock.Rvc;

/// <summary>
/// 一个音频设备引用——索引 + 名字双份。
/// <para>Windows 端点名随驱动可变，配置里同时存索引与名字：名字对得上按名字取，对不上按索引兜底并在面板出声。</para>
/// </summary>
public sealed class RvcRealtimeDevice
{
    /// <summary>设备索引（所属 hostapi 内的编号）。</summary>
    public int Index { get; set; }

    /// <summary>设备名（系统端点原样）。</summary>
    public string Name { get; set; } = "";

    /// <summary>是否虚拟声卡端点（送声给其他软件时输出走 CABLE Input）。</summary>
    public bool Cable { get; set; }
}

/// <summary>
/// 桥接用的 VITS 设置——单一真相源 = VITS 页签的当前配置（模型 / 原说话人 / 目标说话人），
/// RVC 配置里不另存一份，改了那边由服务层推 update 到在跑的流。
/// </summary>
public sealed class RvcVitsBridgeSpec
{
    /// <summary>VITS 模型名（模型根下 &lt;分组&gt;\&lt;名字&gt;.pth）。</summary>
    public string Model { get; set; } = "";

    /// <summary>原说话人索引。</summary>
    public int Src { get; set; }

    /// <summary>目标说话人索引。</summary>
    public int Dst { get; set; }

    /// <summary>自动挑原说话人（按 F0 名册挑与 RVC 输出最接近的；Src 只在关掉自动时用）。</summary>
    public bool SrcAuto { get; set; }

    /// <summary>自动变调（按 RVC 输出实测 F0 反推 f0UpKey，让输出落到目标说话人的音域）。</summary>
    public bool F0Auto { get; set; }

    /// <summary>目标说话人的自然 F0（Hz；名册里没有则 0 = 自动变调不可用）。</summary>
    public double DstF0 { get; set; }

    /// <summary>F0 名册文件路径（worker 侧直接读；空 = 没有名册）。</summary>
    public string F0Profile { get; set; } = "";

    /// <summary>VITS 模型根（ComfyUI\models\VITS）。</summary>
    public string ModelsRoot { get; set; } = "";

    /// <summary>VITS 运行目录（runtime\vits——含 vits_worker.py 与 vits_lite）。</summary>
    public string Repo { get; set; } = "";

    /// <summary>推理设备（cuda / cpu）。</summary>
    public string Device { get; set; } = "cuda";
}

/// <summary>
/// 桥接用的 VITS 运行面——模型根 / 推理代码 / 推理设备 / F0 名册路径 / F0 查询，由 VitsService 提供。
/// <para>路径与名册属「环境」，桥接的**模型与目标说话人**属 RVC 自己的配置（v0.46 起单一真相源在 RVC 页签）。</para>
/// </summary>
public sealed class RvcVitsRuntime
{
    /// <summary>VITS 模型根（ComfyUI\models\VITS）。</summary>
    public string ModelsRoot { get; set; } = "";

    /// <summary>VITS 运行目录（runtime\vits——含 vits_worker.py 与 vits_lite）。</summary>
    public string Repo { get; set; } = "";

    /// <summary>推理设备（cuda / cpu）。</summary>
    public string Device { get; set; } = "cuda";

    /// <summary>F0 名册文件路径（空 = 还没量过）。</summary>
    public string F0Profile { get; set; } = "";

    /// <summary>查某个说话人的自然 F0（模型名, 说话人索引 → Hz；缺了给 0）。</summary>
    public Func<string, int, double>? F0Of { get; set; }
}

/// <summary>
/// 一个 VITS 模型条目（RVC 页签的桥接下拉用）——名字 / 说话人数 / 原生采样率 / 是否可用。
/// </summary>
public sealed class RvcVitsModelItem
{
    /// <summary>模型名（文件名去后缀）。</summary>
    public string Name { get; set; } = "";

    /// <summary>分组（模型根下的一层子目录）。</summary>
    public string Group { get; set; } = "";

    /// <summary>说话人数。</summary>
    public int Speakers { get; set; }

    /// <summary>原生采样率。</summary>
    public int SamplingRate { get; set; }

    /// <summary>是否可用（.pth 与 .json 成对）。</summary>
    public bool Ok { get; set; }
}

/// <summary>一个 VITS 说话人条目（RVC 页签的桥接下拉用）——索引 / 名字 / 自然 F0。</summary>
public sealed class RvcVitsSpeakerItem
{
    /// <summary>说话人索引。</summary>
    public int Index { get; set; }

    /// <summary>名字（模型名册原文；无名则空）。</summary>
    public string Name { get; set; } = "";

    /// <summary>自然 F0（Hz；0 = 名册里还没量到）。</summary>
    public double F0 { get; set; }
}

/// <summary>
/// 实时变声的持久化配置（落盘 <c>data\rvc-realtime-config.json</c>）。
/// <para>字段分两组：热更字段（变调 / formant / 检索混合 / 阈值 / 降噪 / 干声开关）运行时直接改；其余（设备 / 采样率 / F0 取法 / 精度 / 音色）改动要重开流。</para>
/// </summary>
public sealed class RvcRealtimeConfig
{
    /// <summary>设备类型（sounddevice 的 hostapi 编号；-1 = 系统默认）。</summary>
    public int HostApiIndex { get; set; } = -1;

    /// <summary>输入设备（麦克风端点）。</summary>
    public RvcRealtimeDevice Input { get; set; } = new RvcRealtimeDevice();

    /// <summary>输出设备（耳机 / 虚拟声卡端点）。</summary>
    public RvcRealtimeDevice Output { get; set; } = new RvcRealtimeDevice();

    /// <summary>WASAPI 独占模式（降低设备缓冲；独占时其他软件拿不到该端点）。</summary>
    public bool Exclusive { get; set; }

    /// <summary>设备采样率（0 = 跟随音色模型的原生采样率）。</summary>
    public int SampleRate { get; set; } = 48000;

    /// <summary>音色模型文件名（weights 目录下的 .pth）。</summary>
    public string Model { get; set; } = "";

    /// <summary>说话人 id（模型内置音色编号，多数模型只有 0）。</summary>
    public int Sid { get; set; }

    /// <summary>变调半音数（男→女约 +12，女→男约 -12）。</summary>
    public double F0UpKey { get; set; }

    /// <summary>共振峰偏移（声线粗细；负 = 更细，正 = 更粗）。</summary>
    public double Formant { get; set; }

    /// <summary>检索混合比例（0 = 不挂索引；越高越贴训练音色、咬字略糊）。</summary>
    public double IndexRate { get; set; } = 0.75;

    /// <summary>响应阈值（dB，-60 ~ 0；静音判定门限，低于它视为没在说话。默认 -50，调高更难触发）。</summary>
    public double Threshold { get; set; } = -50;

    /// <summary>响度混合比例（0 = 全用输出响度；1 = 全跟输入起伏）。</summary>
    public double RmsMixRate { get; set; } = 0.25;

    /// <summary>清辅音保护（0.5 = 关闭；越小保护越强）。</summary>
    public double Protect { get; set; } = 0.33;

    /// <summary>F0 取法：rmvpe / pm / harvest / crepe（fcpe 不接）。</summary>
    public string F0Method { get; set; } = "rmvpe";

    /// <summary>采样块时长（秒）——延迟与稳定性的主旋钮。</summary>
    public double BlockTime { get; set; } = 0.25;

    /// <summary>块间淡入淡出时长（秒）。</summary>
    public double Crossfade { get; set; } = 0.05;

    /// <summary>额外推理时长（秒）——多给模型一点上下文，换更稳的音高。</summary>
    public double ExtraTime { get; set; } = 2.5;

    /// <summary>输入降噪（TorchGate，零新依赖）。</summary>
    public bool InputDenoise { get; set; }

    /// <summary>输出降噪（TorchGate）。</summary>
    public bool OutputDenoise { get; set; }

    /// <summary>启用相位声码器（原版 use_pv 同口径）。</summary>
    public bool UsePv { get; set; }

    /// <summary>harvest 进程数（F0 取法为 harvest 时生效）。</summary>
    public int HarvestProcesses { get; set; } = 4;

    /// <summary>干声直通（true = 麦克风原声直出，旁路推理；false = 走实时变声）。</summary>
    public bool Bypass { get; set; }

    /// <summary>桥接 VITS 语音转换（true = RVC 输出再经一轮 VITS 说话人转换后才发声）。</summary>
    public bool VcBridge { get; set; }

    /// <summary>桥接攒块数（1 = 每块一次推理；N = 攒 N 块一次；热更不重开流）。</summary>
    public int VcBridgeBlocks { get; set; } = 1;

    /// <summary>桥接用的 VITS 模型名（模型根下 &lt;分组&gt;\&lt;名字&gt;.pth 的名字）。</summary>
    public string VcBridgeModel { get; set; } = "";

    /// <summary>桥接的目标说话人索引（出来的音色）。</summary>
    public int VcBridgeDst { get; set; }

    /// <summary>桥接自动挑原说话人（按 F0 名册挑与 RVC 输出最接近的）。</summary>
    public bool VcBridgeAutoSrc { get; set; } = true;

    /// <summary>桥接自动变调（按目标说话人的自然音域反推 f0UpKey）。</summary>
    public bool VcBridgeAutoF0 { get; set; } = true;

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
/// 实时链的行协议契约——C# 侧与 Python worker 双方唯一口径（请求 cmd / 事件 ev / 热更字段）。
/// <para>协议：stdin 一行一个 JSON 请求，stdout 一行一个 JSON 事件；fd1 让给 stderr，库日志不进协议。</para>
/// </summary>
public static class RvcRealtimeProtocol
{
    /// <summary>请求：枚举设备（hostapi → 设备清单）。</summary>
    public const string CmdDevices = "devices";

    /// <summary>请求：开流（含全部参数）。</summary>
    public const string CmdStart = "start";

    /// <summary>请求：停流（保持进程存活）。</summary>
    public const string CmdStop = "stop";

    /// <summary>请求：热更（只带要改的字段）。</summary>
    public const string CmdUpdate = "update";

    /// <summary>请求：自检（3 秒正弦进链，打印延迟）。</summary>
    public const string CmdSelftest = "selftest";

    /// <summary>请求：退出进程。</summary>
    public const string CmdQuit = "quit";

    /// <summary>事件：设备清单。</summary>
    public const string EvDevices = "devices";

    /// <summary>事件：进程就绪（HuBERT 加载完成，含 torch / 设备自述）。</summary>
    public const string EvReady = "ready";

    /// <summary>事件：开流成功（含模型名与延迟分解）。</summary>
    public const string EvStarted = "started";

    /// <summary>事件：停流完成。</summary>
    public const string EvStopped = "stopped";

    /// <summary>事件：热更完成（附 applied / rejected）。</summary>
    public const string EvUpdated = "updated";

    /// <summary>事件：电平（输入 / 输出 dB）。</summary>
    public const string EvLevel = "level";

    /// <summary>事件：延迟与每块推理耗时。</summary>
    public const string EvLatency = "latency";

    /// <summary>事件：自检结果（独立进程跑）。</summary>
    public const string EvSelftest = "selftest";

    /// <summary>事件：错误（附阶段与人话）。</summary>
    public const string EvError = "error";

    /// <summary>热更白名单——worker 侧的键名（f0UpKey / formant / indexRate / threhold / 降噪 / bypass）。</summary>
    public static readonly string[] HotFields =
    {
        "f0UpKey",
        "formant",
        "indexRate",
        "threhold",
        "inputNoiseReduce",
        "outputNoiseReduce",
        "bypass",
    };
}

/// <summary>
/// 实时链的运行态——面板照它显示这一路的死活与延迟。
/// </summary>
public sealed class RvcRealtimeState
{
    /// <summary>状态：stopped / starting / running / error。</summary>
    public string State { get; set; } = "stopped";

    /// <summary>人话说明。</summary>
    public string Message { get; set; } = "";

    /// <summary>实际采样率（开流后才有）。</summary>
    public int SampleRate { get; set; }

    /// <summary>算法延迟分解：块时长（毫秒）。</summary>
    public double BlockMs { get; set; }

    /// <summary>算法延迟分解：crossfade 拼接（毫秒）。</summary>
    public double CrossfadeMs { get; set; }

    /// <summary>算法延迟分解：额外推理时长（毫秒）。</summary>
    public double ExtraMs { get; set; }

    /// <summary>算法延迟分解：设备缓冲（毫秒）。</summary>
    public double DeviceMs { get; set; }

    /// <summary>算法延迟合计（毫秒，含设备）。</summary>
    public double LatencyMs { get; set; }

    /// <summary>每块推理耗时（毫秒）。</summary>
    public double InferMs { get; set; }

    /// <summary>输入电平（dB）。</summary>
    public double LevelInDb { get; set; }

    /// <summary>输出电平（dB）。</summary>
    public double LevelOutDb { get; set; }

    /// <summary>worker 自述（torch / 设备 / 精度，就绪事件给）。</summary>
    public string Worker { get; set; } = "";

    /// <summary>干声直通（true = 旁路推理）。</summary>
    public bool Bypass { get; set; }

    /// <summary>桥接 VITS 语音转换是否在跑。</summary>
    public bool BridgeEnabled { get; set; }

    /// <summary>桥接攒块数（1 = 每块一次推理）。</summary>
    public int BridgeBlocks { get; set; }

    /// <summary>桥接状态：loading（加载模型）/ ready / error。</summary>
    public string BridgeState { get; set; } = "";

    /// <summary>桥接状态说明（出错原因）。</summary>
    public string BridgeMessage { get; set; } = "";

    /// <summary>桥接用的 VITS 模型名。</summary>
    public string BridgeModel { get; set; } = "";

    /// <summary>桥接的原说话人索引。</summary>
    public int BridgeSrc { get; set; }

    /// <summary>桥接的目标说话人索引。</summary>
    public int BridgeDst { get; set; }

    /// <summary>桥接算法延迟（毫秒：攒块 + 播放队列 + 推理）。</summary>
    public double BridgeMs { get; set; }

    /// <summary>桥接每段推理耗时（毫秒）。</summary>
    public double BridgeInferMs { get; set; }

    /// <summary>桥接欠载次数（推理跟不上实时 → 输出静音的次数）。</summary>
    public long BridgeUnderruns { get; set; }

    /// <summary>桥接是否在自动挑原说话人。</summary>
    public bool BridgeSrcAuto { get; set; }

    /// <summary>自动挑出来的源说话人（BridgeSrcAuto 时面板显示它，而不是配置里的 BridgeSrc）。</summary>
    public int BridgeSrcPicked { get; set; }

    /// <summary>RVC 输出实测基频（Hz；0 = 还没测到）。</summary>
    public double BridgeF0In { get; set; }

    /// <summary>自动变调后的实际 f0UpKey（半音）。</summary>
    public double BridgeUpKey { get; set; }

    /// <summary>目标说话人的自然 F0（Hz；0 = 名册里没有，自动变调不可用）。</summary>
    public double BridgeDstF0 { get; set; }

    /// <summary>桥接日志尾巴（自动挑源 / 自动变调 / 缺名册等可观测事件，逐条带时刻）。</summary>
    public List<string> BridgeLog { get; set; } = new List<string>();
    /// <summary>当前实际在跑的音色模型文件名（开流后才有；换音色重开流会跟着变）。</summary>
    public string Model { get; set; } = "";

    /// <summary>本次开流时刻。</summary>
    public string StartedAt { get; set; } = "";

    /// <summary>worker 的 stderr 尾巴（出错时给人看）。</summary>
    public List<string> StderrTail { get; set; } = new List<string>();
}

/// <summary>
/// 一个 hostapi（设备类型）下的设备清单——面板「设备类型」下拉用。
/// </summary>
public sealed class RvcHostApiItem
{
    /// <summary>hostapi 编号（sounddevice 口径）。</summary>
    public int Index { get; set; }

    /// <summary>hostapi 名（MME / WASAPI / ASIO 等）。</summary>
    public string Name { get; set; } = "";

    /// <summary>默认输入设备编号（-1 = 无）。</summary>
    public int DefaultInput { get; set; } = -1;

    /// <summary>默认输出设备编号（-1 = 无）。</summary>
    public int DefaultOutput { get; set; } = -1;

    /// <summary>该 hostapi 下的设备。</summary>
    public List<RvcDeviceItem> Devices { get; set; } = new List<RvcDeviceItem>();
}

/// <summary>
/// 一个音频设备条目（面板输入 / 输出下拉用）。
/// </summary>
public sealed class RvcDeviceItem
{
    /// <summary>设备索引（hostapi 内编号）。</summary>
    public int Index { get; set; }

    /// <summary>设备名（系统端点原样）。</summary>
    public string Name { get; set; } = "";

    /// <summary>可作输入。</summary>
    public bool In { get; set; }

    /// <summary>可作输出。</summary>
    public bool Out { get; set; }

    /// <summary>是否虚拟声卡端点（VB-CABLE 的 CABLE Input / CABLE Output）。</summary>
    public bool Cable { get; set; }

    /// <summary>是否该 hostapi 的默认设备。</summary>
    public bool Default { get; set; }
}

/// <summary>
/// 实时链的环境实况——解释器 / 推理代码 / worker 脚本 / sounddevice / TorchGate / 音色模型，全部来自磁盘实测。
/// <para>缺什么红着说话：<see cref="Blockers"/> 逐条列，<see cref="CanStart"/> 为 false 时不假装可用。</para>
/// </summary>
public sealed class RvcRealtimeEnvSnapshot
{
    /// <summary>解释器在位。</summary>
    public bool PythonOk { get; set; }

    /// <summary>推理代码（rvc_lite）在位。</summary>
    public bool RepoOk { get; set; }

    /// <summary>worker 脚本在位（data\rvc\rvc_realtime_worker.py）。</summary>
    public bool ScriptOk { get; set; }

    /// <summary>sounddevice 依赖在位（实时链前提）。</summary>
    public bool SoundDeviceOk { get; set; }

    /// <summary>TorchGate 降噪实现在位（runtime\rvc\torchgate.py）。</summary>
    public bool TorchGateOk { get; set; }

    /// <summary>音色模型个数。</summary>
    public int ModelCount { get; set; }

    /// <summary>能不能开流。</summary>
    public bool CanStart { get; set; }

    /// <summary>缺什么（人话，逐条）。</summary>
    public List<string> Blockers { get; set; } = new List<string>();

    /// <summary>探测时刻。</summary>
    public string CheckedAt { get; set; } = "";
}
