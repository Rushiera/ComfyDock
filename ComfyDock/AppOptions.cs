using System;
using System.IO;

namespace ComfyDock;

/// <summary>
/// 命令行选项——全部可覆盖，默认值面向本机现状（D:\done\google + 系统 chrome）。
/// </summary>
public sealed class AppOptions
{
    /// <summary>快捷方式所在目录。</summary>
    public string GoogleRoot { get; set; } = @"D:\done\google";

    /// <summary>chrome.exe 路径。</summary>
    public string ChromePath { get; set; } = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

    /// <summary>数据目录（accounts.json 所在）。</summary>
    public string DataDir { get; set; } = "";

    /// <summary>面板监听端口。</summary>
    public int Port { get; set; } = 8971;

    /// <summary>采集目标页。</summary>
    public string Url { get; set; } = "https://www.runninghub.cn/";

    /// <summary>工作台页（工作流列表来源）。</summary>
    public string WorkspaceUrl { get; set; } = "https://www.runninghub.cn/workspace";
    /// <summary>本地 ComfyUI 地址（探测状态与打开界面都用它）。</summary>
    public string LocalComfyUrl { get; set; } = "http://127.0.0.1:8188/";
    /// <summary>本地 ComfyUI 启动脚本（未运行时一键启动；工作目录取脚本所在目录）。</summary>
    public string LocalComfyLauncher { get; set; } = @"D:\done\ComfyUI_Wan22_Ultra_GGUFv2.33\运行.bat";

    /// <summary>本地 ComfyUI 根目录（含 main.py；MOSS 依赖落点与插件安装都以它为准）。</summary>
    public string LocalComfyRoot { get; set; } = @"D:\done\ComfyUI_Wan22_Ultra_GGUFv2.33\ComfyUI";

    /// <summary>本地生成（MOSS）产物落点目录（默认 exe 同级 out\moss）。</summary>
    public string MossOutDir { get; set; } = "";

    /// <summary>本地生成（IrodoriTTS）——解释器：直接用本机 ComfyUI 的 python_embeded（不再单独装运行时）。</summary>
    public string TtsPython { get; set; } = @"D:\done\ComfyUI_Wan22_Ultra_GGUFv2.33\python_embeded\python.exe";

    /// <summary>本地生成（IrodoriTTS）——Irodori-TTS 代码仓库根目录（worker 的 sys.path 落点；放在 ComfyUI 目录下）。</summary>
    public string TtsRepo { get; set; } = @"D:\done\ComfyUI_Wan22_Ultra_GGUFv2.33\ComfyUI\irodori-tts";

    /// <summary>本地生成（IrodoriTTS）——依田芳乃 · 小模型基座（v4.1-Small）。</summary>
    public string TtsBaseModel { get; set; } = @"D:\done\ComfyUI_Wan22_Ultra_GGUFv2.33\ComfyUI\models\TTS\yoshino\base-v4.1-Small\model.safetensors";

    /// <summary>本地生成（IrodoriTTS）——声码器 weights.pth。</summary>
    public string TtsCodec { get; set; } = @"D:\done\ComfyUI_Wan22_Ultra_GGUFv2.33\ComfyUI\models\TTS\yoshino\codec-dacvae-32dim\weights.pth";

    /// <summary>本地生成（IrodoriTTS）——依田芳乃 · 小模型 LoRA（rank 16）。</summary>
    public string TtsLora { get; set; } = @"D:\done\ComfyUI_Wan22_Ultra_GGUFv2.33\ComfyUI\models\TTS\yoshino\lora-small";

    /// <summary>本地生成（IrodoriTTS）——依田芳乃 · 大模型基座（v4-Large bf16，3.29B）。</summary>
    public string TtsLargeBaseModel { get; set; } = @"D:\done\ComfyUI_Wan22_Ultra_GGUFv2.33\ComfyUI\models\TTS\yoshino\base-v4-Large\model.safetensors";

    /// <summary>本地生成（IrodoriTTS）——依田芳乃 · 大模型 int8 量化基座（8 G 卡推荐）。</summary>
    public string TtsLargeInt8BaseModel { get; set; } = @"D:\done\ComfyUI_Wan22_Ultra_GGUFv2.33\ComfyUI\models\TTS\yoshino\base-v4-Large-int8\int8-weight-only\model.safetensors";

    /// <summary>本地生成（IrodoriTTS）——依田芳乃 · 大模型 LoRA（rank 32）。</summary>
    public string TtsLargeLora { get; set; } = @"D:\done\ComfyUI_Wan22_Ultra_GGUFv2.33\ComfyUI\models\TTS\yoshino\lora-large";

    /// <summary>本地生成（IrodoriTTS）——参考音频目录。</summary>
    public string TtsRefsDir { get; set; } = @"D:\done\ComfyUI_Wan22_Ultra_GGUFv2.33\ComfyUI\models\TTS\yoshino\references";

    /// <summary>本地生成（IrodoriTTS）产物落点目录（默认 exe 同级 out\tts）。</summary>
    public string TtsOutDir { get; set; } = "";

    /// <summary>本地生成（RVC 变声）——解释器：同样直接用本机 ComfyUI 的 python_embeded。</summary>
    public string RvcPython { get; set; } = @"D:\done\ComfyUI_Wan22_Ultra_GGUFv2.33\python_embeded\python.exe";

    /// <summary>本地生成（RVC 变声）——推理代码目录（含 rvc_lite 包，随 ComfyDock 走）。</summary>
    public string RvcRepo { get; set; } = @"D:\Mau\WorkSpace\ComfyDock\runtime\rvc";

    /// <summary>本地生成（RVC 变声）——模型根目录（ComfyUI\models\RVC：weights / indices / hubert / rmvpe）。</summary>
    public string RvcModels { get; set; } = @"D:\done\ComfyUI_Wan22_Ultra_GGUFv2.33\ComfyUI\models\RVC";

    /// <summary>本地生成（RVC 变声）产物落点目录（默认 exe 同级 out\rvc）。</summary>
    public string RvcOutDir { get; set; } = "";

    /// <summary>RVC 自检 CLI——给一段音频，跑一遍变声后打印结果并退出；空 = 不起该分支。</summary>
    public string RvcSelfTest { get; set; } = "";

    /// <summary>RVC 自检 CLI——指定音色模型文件名（空 = weights 里第一个）。</summary>
    public string RvcModel { get; set; } = "";

    /// <summary>本地生成（Qwen3-Omni 转写）——引擎：llama.cpp 的 llama-mtmd-cli（同目录 DLL 一起）。</summary>
    public string OmniCli { get; set; } = @"D:\Mau\WorkSpace\协商知识库\tools\llama\bin\llama-mtmd-cli.exe";

    /// <summary>本地生成（Qwen3-Omni 转写）——主模型（模型住本机 ComfyUI 的 models\LLM）。</summary>
    public string OmniModel { get; set; } = @"D:\done\ComfyUI_Wan22_Ultra_GGUFv2.33\ComfyUI\models\LLM\Qwen3-Omni-30B-A3B-Instruct-Q4_K_M.gguf";

    /// <summary>本地生成（Qwen3-Omni 转写）——音频编码器（mmproj，音频入口在这里）。</summary>
    public string OmniMmproj { get; set; } = @"D:\done\ComfyUI_Wan22_Ultra_GGUFv2.33\ComfyUI\models\LLM\mmproj-Qwen3-Omni-30B-A3B-Instruct-bf16.gguf";

    /// <summary>本地生成（Qwen3-Omni 转写）——解释器：借 ComfyUI 的 python_embeded 探音频时长。</summary>
    public string OmniPython { get; set; } = @"D:\done\ComfyUI_Wan22_Ultra_GGUFv2.33\python_embeded\python.exe";

    /// <summary>本地生成（Qwen3-Omni 转写）产物落点目录（默认 exe 同级 out\omni）。</summary>
    public string OmniOutDir { get; set; } = "";

    /// <summary>Qwen3-Omni 自检 CLI——给一段音频，跑一遍转写后打印结果并退出；空 = 不起该分支。</summary>
    public string OmniSelfTest { get; set; } = "";


    /// <summary>WD Tagger 自检 CLI——给一张图片，跑一遍打标后打印 JSON 并退出；空 = 不起该分支。</summary>
    public string TaggerSelfTest { get; set; } = "";

    /// <summary>本地 ComfyUI 工作流目录（待上传的工作流来源）。</summary>
    public string LocalWorkflowsRoot { get; set; } = @"D:\done\ComfyUI_Wan22_Ultra_GGUFv2.33\ComfyUI\user\default\workflows";

    /// <summary>线上识别产物落点目录（默认 exe 同级 out）。</summary>
    public string RhOutDir { get; set; } = "";

    /// <summary>线上识别 CLI——提交一条音频（完整路径）后退出；空 = 不起 CLI 分支。</summary>
    public string RhSubmit { get; set; } = "";

    /// <summary>线上识别 CLI——提交目标工作区 id（空 = 第一条工作区）。</summary>
    public string RhWorkspace { get; set; } = "";

    /// <summary>线上识别 CLI——轮询一轮后退出。</summary>
    public bool RhPoll { get; set; }

    /// <summary>线上识别 CLI——打印状态后退出。</summary>
    public bool RhStatus { get; set; }

    /// <summary>latent 解码 CLI——把 safetensors latent 解成 png 后退出；空 = 不起该分支。</summary>
    public string LatentDecode { get; set; } = "";
    /// <summary>自建中间态 CLI——把 safetensors latent 转成 `.cdlat` 后退出；空 = 不起该分支。</summary>
    public string CdlatPack { get; set; } = "";
    /// <summary>自建中间态 CLI——把 `.cdlat` 还原成 png 后退出；空 = 不起该分支。</summary>
    public string CdlatUnpack { get; set; } = "";

    /// <summary>采集并发度（默认 2——并行采集；历史实测并发 3（dump-dom 版）与系统内既有 chrome 进程争抢明显，故从 2 起步）。</summary>
    public int Concurrency { get; set; } = 2;

    /// <summary>启动后不自动打开浏览器。</summary>
    public bool NoBrowser { get; set; }

    /// <summary>快照文件完整路径。</summary>
    public string SnapshotPath => Path.Combine(DataDir, "accounts.json");

    /// <summary>解析命令行参数。</summary>
    /// <param name="args">原始参数数组。</param>
    /// <returns>选项对象；未知参数或非法值抛异常。</returns>
    public static AppOptions Parse(string[] args)
    {
        var options = new AppOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            switch (key)
            {
                case "--google-root":
                    options.GoogleRoot = RequireValue(args, ref i, key);
                    break;
                case "--chrome":
                    options.ChromePath = RequireValue(args, ref i, key);
                    break;
                case "--data":
                    options.DataDir = RequireValue(args, ref i, key);
                    break;
                case "--port":
                    options.Port = RequireInt(args, ref i, key);
                    break;
                case "--url":
                    options.Url = RequireValue(args, ref i, key);
                    break;
                case "--local-url":
                    options.LocalComfyUrl = RequireValue(args, ref i, key);
                    break;
                case "--local-launcher":
                    options.LocalComfyLauncher = RequireValue(args, ref i, key);
                    break;
                case "--local-comfy-root":
                    options.LocalComfyRoot = RequireValue(args, ref i, key);
                    break;
                case "--moss-out":
                    options.MossOutDir = RequireValue(args, ref i, key);
                    break;
                case "--tts-python":
                    options.TtsPython = RequireValue(args, ref i, key);
                    break;
                case "--tts-repo":
                    options.TtsRepo = RequireValue(args, ref i, key);
                    break;
                case "--tts-base":
                    options.TtsBaseModel = RequireValue(args, ref i, key);
                    break;
                case "--tts-codec":
                    options.TtsCodec = RequireValue(args, ref i, key);
                    break;
                case "--tts-lora":
                    options.TtsLora = RequireValue(args, ref i, key);
                    break;
                case "--tts-large-base":
                    options.TtsLargeBaseModel = RequireValue(args, ref i, key);
                    break;
                case "--tts-large-int8-base":
                    options.TtsLargeInt8BaseModel = RequireValue(args, ref i, key);
                    break;
                case "--tts-large-lora":
                    options.TtsLargeLora = RequireValue(args, ref i, key);
                    break;
                case "--tts-refs":
                    options.TtsRefsDir = RequireValue(args, ref i, key);
                    break;
                case "--tts-out":
                    options.TtsOutDir = RequireValue(args, ref i, key);
                    break;
                case "--rvc-python":
                    options.RvcPython = RequireValue(args, ref i, key);
                    break;
                case "--rvc-repo":
                    options.RvcRepo = RequireValue(args, ref i, key);
                    break;
                case "--rvc-models":
                    options.RvcModels = RequireValue(args, ref i, key);
                    break;
                case "--rvc-out":
                    options.RvcOutDir = RequireValue(args, ref i, key);
                    break;
                case "--rvc-selftest":
                    options.RvcSelfTest = RequireValue(args, ref i, key);
                    break;
                case "--rvc-model":
                    options.RvcModel = RequireValue(args, ref i, key);
                    break;
                case "--omni-cli":
                    options.OmniCli = RequireValue(args, ref i, key);
                    break;
                case "--omni-model":
                    options.OmniModel = RequireValue(args, ref i, key);
                    break;
                case "--omni-mmproj":
                    options.OmniMmproj = RequireValue(args, ref i, key);
                    break;
                case "--omni-python":
                    options.OmniPython = RequireValue(args, ref i, key);
                    break;
                case "--omni-out":
                    options.OmniOutDir = RequireValue(args, ref i, key);
                    break;
                case "--omni-selftest":
                    options.OmniSelfTest = RequireValue(args, ref i, key);
                    break;
                case "--tagger-selftest":
                    options.TaggerSelfTest = RequireValue(args, ref i, key);
                    break;
                case "--concurrency":
                    options.Concurrency = RequireInt(args, ref i, key);
                    break;
                case "--rh-out":
                    options.RhOutDir = RequireValue(args, ref i, key);
                    break;
                case "--rh-submit":
                    options.RhSubmit = RequireValue(args, ref i, key);
                    break;
                case "--rh-workspace":
                    options.RhWorkspace = RequireValue(args, ref i, key);
                    break;
                case "--rh-poll":
                    options.RhPoll = true;
                    break;
                case "--rh-status":
                    options.RhStatus = true;
                    break;
                case "--latent-decode":
                    options.LatentDecode = RequireValue(args, ref i, key);
                    break;
                case "--cdlat-pack":
                    options.CdlatPack = RequireValue(args, ref i, key);
                    break;
                case "--cdlat-unpack":
                    options.CdlatUnpack = RequireValue(args, ref i, key);
                    break;
                case "--no-browser":
                    options.NoBrowser = true;
                    break;
                default:
                    throw new ArgumentException("未知参数：" + key);
            }
        }

        if (options.Concurrency < 1)
        {
            throw new ArgumentException("--concurrency 必须 ≥ 1");
        }

        if (options.Port is < 1 or > 65535)
        {
            throw new ArgumentException("--port 必须在 1..65535");
        }

        if (string.IsNullOrWhiteSpace(options.DataDir))
        {
            options.DataDir = Path.Combine(AppContext.BaseDirectory, "data");
        }

        if (string.IsNullOrWhiteSpace(options.RhOutDir))
        {
            options.RhOutDir = Path.Combine(AppContext.BaseDirectory, "out");
        }

        if (string.IsNullOrWhiteSpace(options.MossOutDir))
        {
            options.MossOutDir = Path.Combine(AppContext.BaseDirectory, "out", "moss");
        }

        if (string.IsNullOrWhiteSpace(options.TtsOutDir))
        {
            options.TtsOutDir = Path.Combine(AppContext.BaseDirectory, "out", "tts");
        }

        if (string.IsNullOrWhiteSpace(options.RvcOutDir))
        {
            options.RvcOutDir = Path.Combine(AppContext.BaseDirectory, "out", "rvc");
        }

        if (string.IsNullOrWhiteSpace(options.OmniOutDir))
        {
            options.OmniOutDir = Path.Combine(AppContext.BaseDirectory, "out", "omni");
        }

        return options;
    }

    private static string RequireValue(string[] args, ref int i, string key)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException("参数缺值：" + key);
        }

        i++;
        return args[i];
    }

    private static int RequireInt(string[] args, ref int i, string key)
    {
        string raw = RequireValue(args, ref i, key);
        if (!int.TryParse(raw, out int value))
        {
            throw new ArgumentException("参数非整数：" + key + " = " + raw);
        }

        return value;
    }
}
