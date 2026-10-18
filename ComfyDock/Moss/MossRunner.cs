using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Moss;

/// <summary>一次生成的全部参数（面板上的那一屏，原样落到节点图）。</summary>
public sealed class MossGenParams
{
    /// <summary>模型组织方式：single（单文件三件套）/ hf（diffusers 目录，走 HF Loader）。</summary>
    public string Mode { get; set; } = "single";

    /// <summary>主模型文件名（diffusion_models 下）。</summary>
    public string UnetName { get; set; } = "";

    /// <summary>文本编码器文件名（text_encoders 下）。</summary>
    public string ClipName { get; set; } = "";

    /// <summary>VAE 文件名（vae 下）。</summary>
    public string VaeName { get; set; } = "";

    /// <summary>diffusers 目录绝对路径（Mode = hf 时用）。</summary>
    public string HfPath { get; set; } = "";

    /// <summary>正向提示词（自然语言 / Danbooru 标签 / Layout JSON 都行）。</summary>
    public string Prompt { get; set; } = "";

    /// <summary>负向提示词（官方推荐留空）。</summary>
    public string Negative { get; set; } = "";

    /// <summary>宽（对齐 16）。</summary>
    public int Width { get; set; } = 1024;

    /// <summary>高（对齐 16）。</summary>
    public int Height { get; set; } = 1024;

    /// <summary>步数。</summary>
    public int Steps { get; set; } = 50;

    /// <summary>CFG（8 步加速时固定 1）。</summary>
    public double Cfg { get; set; } = 3.0;

    /// <summary>Shift（ModelSamplingSD3）。</summary>
    public double Shift { get; set; } = 5.0;

    /// <summary>随机种子（-1 = 本次随机）。</summary>
    public long Seed { get; set; } = -1;

    /// <summary>批次张数。</summary>
    public int Batch { get; set; } = 1;

    /// <summary>是否开 8 步加速（加载 DMD LoRA，步数 8 / CFG 1）。</summary>
    public bool Accel { get; set; }

    /// <summary>加速 LoRA 文件名（loras 下）。</summary>
    public string AccelLora { get; set; } = "";

    /// <summary>加速 LoRA 权重。</summary>
    public double AccelStrength { get; set; } = 1.0;

    /// <summary>是否开结构增强（SLG：负向分支跳过第 22 层）。</summary>
    public bool Slg { get; set; } = true;

    /// <summary>结构增强跳过层（0-index，官方推荐 22）。</summary>
    public string SlgLayers { get; set; } = "22";

    /// <summary>用户 LoRA 文件名（空 = 不用）。</summary>
    public string LoraName { get; set; } = "";

    /// <summary>用户 LoRA 权重。</summary>
    public double LoraStrength { get; set; } = 1.0;

    /// <summary>是否开 Hires 第二遍（Lanczos 像素放大后重绘）。</summary>
    public bool Hires { get; set; }

    /// <summary>Hires 放大倍率。</summary>
    public double HiresScale { get; set; } = 1.5;

    /// <summary>Hires 第二遍重绘幅度。</summary>
    public double HiresDenoise { get; set; } = 0.5;

    /// <summary>图生图输入图片文件名（ComfyUI input 目录内；空 = 文生图）。</summary>
    public string InputImage { get; set; } = "";

    /// <summary>图生图重绘幅度。</summary>
    public double ImgDenoise { get; set; } = 0.6;
}

/// <summary>生成任务状态（面板轮询它）。</summary>
public sealed class MossJobStatus
{
    /// <summary>本软件内的任务号。</summary>
    public string Id { get; set; } = "";

    /// <summary>状态：idle / submitted / running / done / error。</summary>
    public string State { get; set; } = "idle";

    /// <summary>ComfyUI 的 prompt_id。</summary>
    public string PromptId { get; set; } = "";

    /// <summary>说明 / 失败原因。</summary>
    public string Message { get; set; } = "";

    /// <summary>产物文件名（落 out\moss 后的文件名）。</summary>
    public List<string> Images { get; set; } = new List<string>();

    /// <summary>本次种子。</summary>
    public long Seed { get; set; }

    /// <summary>本次步数。</summary>
    public int Steps { get; set; }

    /// <summary>提交时刻。</summary>
    public string SubmittedAt { get; set; } = "";

    /// <summary>结束时刻。</summary>
    public string FinishedAt { get; set; } = "";
}

/// <summary>
/// MOSS 节点图构造与投递——只经 ComfyUI 的 HTTP API（/prompt · /history · /view · /interrupt），
/// 不碰引擎内部文件，不改工作流文件。
/// </summary>
public sealed class MossRunner
{
    /// <summary>HTTP 客户端（复用）。</summary>
    private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>ComfyUI 地址。</summary>
    private readonly string _url;

    /// <summary>以 ComfyUI 地址构造。</summary>
    /// <param name="url">ComfyUI 地址（如 http://127.0.0.1:8188/）。</param>
    public MossRunner(string url)
    {
        _url = url;
    }

    /// <summary>把本地图片拷进 ComfyUI 的 input 目录，返回引擎侧文件名（图生图入口）。</summary>
    /// <param name="sourcePath">本地图片完整路径。</param>
    /// <param name="inputDir">ComfyUI 的 input 目录。</param>
    /// <returns>引擎侧文件名；失败返回空串。</returns>
    public static string StageImage(string sourcePath, string inputDir)
    {
        try
        {
            if (!File.Exists(sourcePath))
            {
                return "";
            }

            Directory.CreateDirectory(inputDir);
            string name = "mossdock_" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture) + "_" + Path.GetFileName(sourcePath);
            File.Copy(sourcePath, Path.Combine(inputDir, name), true);
            return name;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>提交一次生成。</summary>
    /// <param name="p">参数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>prompt_id；失败抛异常。</returns>
    public async Task<string> SubmitAsync(MossGenParams p, CancellationToken ct)
    {
        // [段0] 模型名先按引擎的写法归一——Windows 上子目录是反斜杠，面板里存的是正斜杠，
        //        直接用面板值会被 ComfyUI 判 `value_not_in_list`（实测 400：lora_name 不在列表里）
        p.AccelLora = await ResolveModelNameAsync("LoraLoaderModelOnly", "lora_name", p.AccelLora, ct).ConfigureAwait(false);
        p.LoraName = await ResolveModelNameAsync("LoraLoaderModelOnly", "lora_name", p.LoraName, ct).ConfigureAwait(false);
        p.UnetName = await ResolveModelNameAsync("MOSSImageUNETLoader", "unet_name", p.UnetName, ct).ConfigureAwait(false);
        p.ClipName = await ResolveModelNameAsync("MOSSImageCLIPLoader", "clip_name", p.ClipName, ct).ConfigureAwait(false);
        p.VaeName = await ResolveModelNameAsync("VAELoader", "vae_name", p.VaeName, ct).ConfigureAwait(false);

        JsonObject graph = BuildGraph(p);
        var body = new JsonObject
        {
            ["prompt"] = graph,
            ["client_id"] = "comfydock-moss",
        };

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await Client
            .PostAsync(new Uri(new Uri(_url), "prompt"), content, ct)
            .ConfigureAwait(false);

        string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("ComfyUI 拒绝了节点图（HTTP " + (int)response.StatusCode + "）：" + Shorten(text));
        }

        using JsonDocument doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("error", out JsonElement error))
        {
            throw new InvalidOperationException("ComfyUI 报错：" + Shorten(error.ToString()));
        }

        if (!doc.RootElement.TryGetProperty("prompt_id", out JsonElement id) || id.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("ComfyUI 没回 prompt_id：" + Shorten(text));
        }

        return id.GetString() ?? "";
    }

    /// <summary>查一次任务——完成则回产物清单，未完回运行状态。</summary>
    /// <param name="promptId">引擎侧任务号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(状态, 产物列表, 说明)。</returns>
    public async Task<(string State, List<(string File, string Sub, string Type)> Images, string Message)> PollAsync(
        string promptId,
        CancellationToken ct)
    {
        var images = new List<(string File, string Sub, string Type)>();

        using (HttpResponseMessage response = await Client
            .GetAsync(new Uri(new Uri(_url), "history/" + promptId), ct)
            .ConfigureAwait(false))
        {
            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode && text.Length > 2)
            {
                using JsonDocument doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty(promptId, out JsonElement entry))
                {
                    if (entry.TryGetProperty("status", out JsonElement status)
                        && status.TryGetProperty("status_str", out JsonElement statusStr)
                        && statusStr.ValueKind == JsonValueKind.String
                        && statusStr.GetString() == "error")
                    {
                        return ("error", images, "ComfyUI 执行报错——去 ComfyUI 控制台看那一段堆栈");
                    }

                    if (entry.TryGetProperty("outputs", out JsonElement outputs) && outputs.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty node in outputs.EnumerateObject())
                        {
                            if (!node.Value.TryGetProperty("images", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                            {
                                continue;
                            }

                            foreach (JsonElement image in list.EnumerateArray())
                            {
                                string file = image.TryGetProperty("filename", out JsonElement f) ? (f.GetString() ?? "") : "";
                                string sub = image.TryGetProperty("subfolder", out JsonElement s) ? (s.GetString() ?? "") : "";
                                string type = image.TryGetProperty("type", out JsonElement t) ? (t.GetString() ?? "output") : "output";
                                if (file.Length > 0)
                                {
                                    images.Add((file, sub, type));
                                }
                            }
                        }
                    }

                    if (images.Count > 0)
                    {
                        return ("done", images, "已完成");
                    }
                }
            }
        }

        // 没在历史里——看在不在队列（运行中 / 排队中）
        using HttpResponseMessage queueResponse = await Client
            .GetAsync(new Uri(new Uri(_url), "queue"), ct)
            .ConfigureAwait(false);
        string queueText = await queueResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (queueText.Contains(promptId, StringComparison.Ordinal))
        {
            return ("running", images, "采样中");
        }

        return ("running", images, "等待引擎…");
    }

    /// <summary>把产物从 ComfyUI 拉回本地目录。</summary>
    /// <param name="images">产物清单。</param>
    /// <param name="outDir">本地落点目录。</param>
    /// <param name="seed">本次种子（用作文件名前缀）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>落盘文件名列表。</returns>
    public async Task<List<string>> FetchAsync(
        List<(string File, string Sub, string Type)> images,
        string outDir,
        long seed,
        CancellationToken ct)
    {
        Directory.CreateDirectory(outDir);
        var saved = new List<string>();
        int index = 0;

        foreach ((string file, string sub, string type) in images)
        {
            index++;
            string query = "view?filename=" + Uri.EscapeDataString(file)
                + "&subfolder=" + Uri.EscapeDataString(sub)
                + "&type=" + Uri.EscapeDataString(type);

            using HttpResponseMessage response = await Client
                .GetAsync(new Uri(new Uri(_url), query), ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            string name = seed + (index > 1 ? "_" + index.ToString(CultureInfo.InvariantCulture) : "") + ".png";
            await File.WriteAllBytesAsync(Path.Combine(outDir, name), bytes, ct).ConfigureAwait(false);
            saved.Add(name);
        }

        return saved;
    }

    /// <summary>中断当前任务。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>无。</returns>
    public async Task InterruptAsync(CancellationToken ct)
    {
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await Client
            .PostAsync(new Uri(new Uri(_url), "interrupt"), content, ct)
            .ConfigureAwait(false);
    }

    /// <summary>把长文本压短（错误回显用）。</summary>
    /// <param name="text">原文。</param>
    /// <returns>≤ 600 字符。</returns>
    private static string Shorten(string text)
    {
        if (text.Length <= 600)
        {
            return text;
        }

        return text.Substring(0, 600) + "…";
    }

    /// <summary>构造节点图——官方推荐 50 步 / CFG 3 / Shift 5 / Euler + sgm_uniform / SLG 跳过 22 层。</summary>
    /// <param name="p">参数。</param>
    /// <returns>API 格式的节点图。</returns>
    private static JsonObject BuildGraph(MossGenParams p)
    {
        var graph = new JsonObject();
        JsonArray modelRef;
        JsonArray clipRef;
        JsonArray vaeRef;

        if (string.Equals(p.Mode, "hf", StringComparison.OrdinalIgnoreCase))
        {
            // diffusers 目录——一个 HF Loader 同时出 MODEL / CLIP / VAE
            graph["1"] = Node("MOSSImageHFLoader", new JsonObject
            {
                ["model_path"] = p.HfPath,
                ["weight_dtype"] = "default",
                ["text_device"] = "default",
            }, "MOSS-Image HF Loader");
            modelRef = new JsonArray("1", 0);
            clipRef = new JsonArray("1", 1);
            vaeRef = new JsonArray("1", 2);
        }
        else
        {
            graph["1"] = Node("MOSSImageUNETLoader", new JsonObject
            {
                ["unet_name"] = p.UnetName,
                ["weight_dtype"] = "default",
            }, "MOSS-Image Load Diffusion Model");
            graph["2"] = Node("MOSSImageCLIPLoader", new JsonObject
            {
                ["clip_name"] = p.ClipName,
                ["device"] = "default",
            }, "MOSS-Image Load Qwen3.5");
            graph["3"] = Node("VAELoader", new JsonObject
            {
                ["vae_name"] = p.VaeName,
            }, "Load VAE");
            modelRef = new JsonArray("1", 0);
            clipRef = new JsonArray("2", 0);
            vaeRef = new JsonArray("3", 0);
        }

        // 加速 LoRA 先挂（作用于主模型），再挂用户 LoRA
        string next = "20";
        if (p.Accel && p.AccelLora.Length > 0)
        {
            graph[next] = Node("LoraLoaderModelOnly", new JsonObject
            {
                ["model"] = modelRef.DeepClone(),
                ["lora_name"] = p.AccelLora,
                ["strength_model"] = p.AccelStrength,
            }, "8 步加速 LoRA（DMD）");
            modelRef = new JsonArray(next, 0);
            next = "21";
        }

        if (p.LoraName.Length > 0)
        {
            graph[next] = Node("LoraLoaderModelOnly", new JsonObject
            {
                ["model"] = modelRef.DeepClone(),
                ["lora_name"] = p.LoraName,
                ["strength_model"] = p.LoraStrength,
            }, "LoRA");
            modelRef = new JsonArray(next, 0);
        }

        // Shift 必须在 SLG 之前——调度与 SLG 阈值共用同一个 shift
        graph["4"] = Node("ModelSamplingSD3", new JsonObject
        {
            ["shift"] = p.Shift,
            ["model"] = modelRef.DeepClone(),
        }, "官方 Flow sigma shift");
        JsonArray samplingRef = new JsonArray("4", 0);

        if (p.Slg)
        {
            graph["5"] = Node("SkipLayerGuidanceDiTSimple", new JsonObject
            {
                ["double_layers"] = "",
                ["single_layers"] = p.SlgLayers,
                ["start_percent"] = 0.0,
                ["end_percent"] = 1.0,
                ["model"] = samplingRef.DeepClone(),
            }, "结构增强（uncond skip）");
            samplingRef = new JsonArray("5", 0);
        }

        graph["6"] = Node("CLIPTextEncode", new JsonObject
        {
            ["text"] = p.Prompt,
            ["clip"] = clipRef.DeepClone(),
        }, "正向提示词");
        graph["7"] = Node("CLIPTextEncode", new JsonObject
        {
            ["text"] = p.Negative,
            ["clip"] = clipRef.DeepClone(),
        }, "负向提示词");

        // 输入侧：图生图走 LoadImage + VAEEncode，文生图走空 latent
        string latentRef;
        if (p.InputImage.Length > 0)
        {
            graph["30"] = Node("LoadImage", new JsonObject
            {
                ["image"] = p.InputImage,
            }, "输入图片");
            graph["31"] = Node("VAEEncode", new JsonObject
            {
                ["pixels"] = new JsonArray("30", 0),
                ["vae"] = vaeRef.DeepClone(),
            }, "VAE 编码");
            latentRef = "31";
        }
        else
        {
            graph["8"] = Node("EmptyFlux2LatentImage", new JsonObject
            {
                ["width"] = p.Width,
                ["height"] = p.Height,
                ["batch_size"] = p.Batch,
            }, "空 Flux 2 latent");
            latentRef = "8";
        }

        double denoise = p.InputImage.Length > 0 ? p.ImgDenoise : 1.0;
        graph["9"] = Node("KSampler", new JsonObject
        {
            ["seed"] = p.Seed,
            ["steps"] = p.Steps,
            ["cfg"] = p.Cfg,
            ["sampler_name"] = "euler",
            ["scheduler"] = "sgm_uniform",
            ["denoise"] = denoise,
            ["model"] = samplingRef.DeepClone(),
            ["positive"] = new JsonArray("6", 0),
            ["negative"] = new JsonArray("7", 0),
            ["latent_image"] = new JsonArray(latentRef, 0),
        }, "KSampler");

        graph["10"] = Node("VAEDecode", new JsonObject
        {
            ["samples"] = new JsonArray("9", 0),
            ["vae"] = vaeRef.DeepClone(),
        }, "VAE Decode");

        string imageRef = "10";
        if (p.Hires)
        {
            int hiresWidth = Align16((int)Math.Round(p.Width * p.HiresScale));
            int hiresHeight = Align16((int)Math.Round(p.Height * p.HiresScale));

            // MOSS 的 latent 不是普通 4 通道格式——放大只能走像素侧 Lanczos
            graph["11"] = Node("ImageScale", new JsonObject
            {
                ["upscale_method"] = "lanczos",
                ["width"] = hiresWidth,
                ["height"] = hiresHeight,
                ["crop"] = "disabled",
                ["image"] = new JsonArray("10", 0),
            }, "Lanczos 放大");
            graph["12"] = Node("VAEEncode", new JsonObject
            {
                ["pixels"] = new JsonArray("11", 0),
                ["vae"] = vaeRef.DeepClone(),
            }, "Hires VAE 编码");
            graph["13"] = Node("KSampler", new JsonObject
            {
                ["seed"] = p.Seed + 1,
                ["steps"] = p.Steps,
                ["cfg"] = p.Cfg,
                ["sampler_name"] = "euler",
                ["scheduler"] = "sgm_uniform",
                ["denoise"] = p.HiresDenoise,
                ["model"] = samplingRef.DeepClone(),
                ["positive"] = new JsonArray("6", 0),
                ["negative"] = new JsonArray("7", 0),
                ["latent_image"] = new JsonArray("12", 0),
            }, "Hires KSampler");
            graph["14"] = Node("VAEDecode", new JsonObject
            {
                ["samples"] = new JsonArray("13", 0),
                ["vae"] = vaeRef.DeepClone(),
            }, "Hires VAE Decode");
            imageRef = "14";
        }

        graph["15"] = Node("SaveImage", new JsonObject
        {
            ["filename_prefix"] = "mossdock/moss",
            ["images"] = new JsonArray(imageRef, 0),
        }, "保存图片");

        return graph;
    }

    /// <summary>造一个节点。</summary>
    /// <param name="classType">节点类名。</param>
    /// <param name="inputs">输入表。</param>
    /// <param name="title">显示标题（只影响 ComfyUI 界面里的名字）。</param>
    /// <returns>节点对象。</returns>
    private static JsonObject Node(string classType, JsonObject inputs, string title)
    {
        return new JsonObject
        {
            ["class_type"] = classType,
            ["inputs"] = inputs,
            ["_meta"] = new JsonObject { ["title"] = title },
        };
    }

    /// <summary>对齐到 16 的倍数（MOSS 的宽高硬性要求）。</summary>
    /// <param name="value">原始值。</param>
    /// <returns>对齐后的值（最小 16）。</returns>
    private static int Align16(int value)
    {
        int aligned = (value / 16) * 16;
        return aligned < 16 ? 16 : aligned;
    }
    /// <summary>
    /// 把面板里的模型名换成引擎认的写法——Windows 上子目录是反斜杠（`MOSS\MOSS-SFT-8step-DMD.safetensors`），
    /// 面板里存的是正斜杠；直接用面板值会被 ComfyUI 判 `value_not_in_list`（实测 400）。
    /// 做法：问引擎要该节点的选项表，先精确匹配、再按文件名匹配，都找不到就原样返回（让引擎自己报错）。
    /// </summary>
    /// <param name="nodeClass">节点类名（如 LoraLoaderModelOnly）。</param>
    /// <param name="inputName">输入名（如 lora_name）。</param>
    /// <param name="wanted">面板里的值（空 = 不处理）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>引擎认的名字。</returns>
    private async Task<string> ResolveModelNameAsync(string nodeClass, string inputName, string wanted, CancellationToken ct)
    {
        if (wanted.Length == 0)
        {
            return wanted;
        }

        try
        {
            using HttpResponseMessage response = await Client
                .GetAsync(new Uri(new Uri(_url), "object_info/" + nodeClass), ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return wanted;
            }

            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty(nodeClass, out JsonElement node))
            {
                return wanted;
            }

            if (!node.TryGetProperty("input", out JsonElement input)
                || !input.TryGetProperty("required", out JsonElement required)
                || !required.TryGetProperty(inputName, out JsonElement spec)
                || spec.ValueKind != JsonValueKind.Array
                || spec.GetArrayLength() < 1)
            {
                return wanted;
            }

            // 两种写法都要认：新版 `["COMBO", {"options": [...]}]`；老版 `[["a","b"], {}]`（核心节点还在用）
            JsonElement list = default;
            bool found = false;
            if (spec.GetArrayLength() >= 2 && spec[1].ValueKind == JsonValueKind.Object
                && spec[1].TryGetProperty("options", out JsonElement options)
                && options.ValueKind == JsonValueKind.Array)
            {
                list = options;
                found = true;
            }
            else if (spec[0].ValueKind == JsonValueKind.Array)
            {
                list = spec[0];
                found = true;
            }

            if (!found)
            {
                return wanted;
            }

            string bare = wanted.Replace('\\', '/');
            int slash = bare.LastIndexOf('/');
            string leaf = slash >= 0 ? bare.Substring(slash + 1) : bare;

            string fallback = "";
            foreach (JsonElement item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string name = item.GetString() ?? "";
                if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }

                string nameBare = name.Replace('\\', '/');
                int nameSlash = nameBare.LastIndexOf('/');
                string nameLeaf = nameSlash >= 0 ? nameBare.Substring(nameSlash + 1) : nameBare;
                if (fallback.Length == 0 && string.Equals(nameLeaf, leaf, StringComparison.OrdinalIgnoreCase))
                {
                    fallback = name;
                }
            }

            return fallback.Length > 0 ? fallback : wanted;
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException || ex is InvalidOperationException)
        {
            return wanted;
        }
    }
}
