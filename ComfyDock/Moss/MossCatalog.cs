using System;
using System.Collections.Generic;

namespace ComfyDock.Moss;

/// <summary>MOSS 依赖文件的落点分类——决定放进 ComfyUI 的哪个模型目录。</summary>
public enum MossTarget
{
    /// <summary>主模型（ComfyUI 的 diffusion_models）。</summary>
    DiffusionModels,

    /// <summary>文本编码器（ComfyUI 的 text_encoders）。</summary>
    TextEncoders,

    /// <summary>VAE（ComfyUI 的 vae）。</summary>
    Vae,

    /// <summary>LoRA（ComfyUI 的 loras 子目录）。</summary>
    Loras,

    /// <summary>diffusers 目录（HF Loader 直接读绝对路径，不落模型目录）。</summary>
    HfDirectory,

    /// <summary>图像编辑 LoRA（models\Moss-Image-Edit——编辑插件按目录读 adapter 两件）。</summary>
    MossEditAdapter,
}

/// <summary>
/// 一条依赖文件——索引的最小单位：从哪个仓库的哪个路径来、多大、落到哪里。
/// 软件本体不打包任何权重，只打包这张索引。
/// </summary>
public sealed class MossFile
{
    /// <summary>索引内唯一 id（面板与下载队列都用它）。</summary>
    public string Id { get; init; } = "";

    /// <summary>面板显示名。</summary>
    public string Title { get; init; } = "";

    /// <summary>Hugging Face 仓库（如 Comfy-Org/MOSS-Image）。</summary>
    public string Repo { get; init; } = "";

    /// <summary>仓库内相对路径（如 diffusion_models/xxx.safetensors）。</summary>
    public string RepoPath { get; init; } = "";

    /// <summary>落点分类。</summary>
    public MossTarget Target { get; init; }

    /// <summary>落点子目录（相对该分类根目录；空 = 直接放根）。</summary>
    public string SubDir { get; init; } = "";

    /// <summary>落盘文件名（空 = 取 RepoPath 的文件名）。</summary>
    public string FileName { get; init; } = "";

    /// <summary>字节数（来自仓库文件清单——用于进度与在位校验）。</summary>
    public long Bytes { get; init; }

    /// <summary>说明 / 用途（面板原样显示）。</summary>
    public string Note { get; init; } = "";

    /// <summary>魔搭镜像仓库（空 = 无镜像，只能走 HF / hf-mirror）。</summary>
    public string ModelScopeRepo { get; init; } = "";

    /// <summary>落盘文件名——FileName 为空时回落到仓库路径末段。</summary>
    /// <returns>文件名。</returns>
    public string ResolvedFileName()
    {
        if (FileName.Length > 0)
        {
            return FileName;
        }

        // diffusers 目录方案按仓库内相对路径落位（transformer/… · text_encoder/… · tokenizer/… · vae/…）——
        // 不这么做，三处同名的 config.json 会互相覆盖
        if (Target == MossTarget.HfDirectory)
        {
            return RepoPath.Replace('/', System.IO.Path.DirectorySeparatorChar);
        }

        int slash = RepoPath.LastIndexOf('/');
        return slash >= 0 ? RepoPath.Substring(slash + 1) : RepoPath;
    }
}

/// <summary>
/// 一套下载方案——按显存档位推荐的依赖组合（主模型 + 文本编码器 + VAE）。
/// </summary>
public sealed class MossPlan
{
    /// <summary>方案 id（面板按钮与配置里存的值）。</summary>
    public string Id { get; init; } = "";

    /// <summary>方案名。</summary>
    public string Title { get; init; } = "";

    /// <summary>方案说明（精度 / 显存 / 体积口径）。</summary>
    public string Note { get; init; } = "";

    /// <summary>该方案需要的文件 id 列表。</summary>
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();

    /// <summary>推荐显存下限（MB）——低于它就别选这个方案。</summary>
    public int MinVramMb { get; init; }

    /// <summary>是否给「一键下载推荐方案」用（可选方案不参与推荐）。</summary>
    public bool Recommended { get; init; }
}

/// <summary>
/// MOSS-Image 依赖索引——软件内自带的唯一真相：需要哪些文件、从哪下、放哪里。
/// 2026-10-18 侦察取证：Comfy-Org/MOSS-Image（单文件 bf16 / int8 convrot + Flux.2 VAE）、
/// OpenMOSS-Team/MOSS-Image-SFT（diffusers 目录）、lodestones/MOSS-Image-SFT-8-steps-dmd（8 步 DMD LoRA）。
/// </summary>
public static class MossCatalog
{
    /// <summary>
    /// 下载源顺序——**魔搭优先**（2026-10-18 实测：同一文件魔搭 12.3 MB/s、hf-mirror 掉到 0.3 MB/s），
    /// 没有镜像的仓库魔搭回 404，下载器自动落下一个源。
    /// </summary>
    public static readonly string[] SourceOrder = { "modelscope", "hf-mirror", "hf" };

    /// <summary>全部索引条目。</summary>
    public static readonly IReadOnlyList<MossFile> Files = new List<MossFile>
    {
        new MossFile
        {
            Id = "main-sft-int8",
            Title = "MOSS-Image SFT 主模型 · INT8 ConvRot",
            Repo = "Comfy-Org/MOSS-Image",
            RepoPath = "diffusion_models/moss_image_sft_int8_convrot.safetensors",
            Target = MossTarget.DiffusionModels,
            Bytes = 5413698958,
            Note = "压缩版主模型（ComfyUI 原生格式）——日常出图推荐；画质接近官方，只压主模型不省显存",
        },
        new MossFile
        {
            Id = "main-sft-bf16",
            Title = "MOSS-Image SFT 主模型 · bf16",
            Repo = "Comfy-Org/MOSS-Image",
            RepoPath = "diffusion_models/moss_image_sft_bf16.safetensors",
            Target = MossTarget.DiffusionModels,
            Bytes = 10164655808,
            Note = "官方精度主模型——大显存用；与官方逐位一致",
        },
        new MossFile
        {
            Id = "main-base-int8",
            Title = "MOSS-Image Base 主模型 · INT8 ConvRot",
            Repo = "Comfy-Org/MOSS-Image",
            RepoPath = "diffusion_models/moss_image_base_int8_convrot.safetensors",
            Target = MossTarget.DiffusionModels,
            Bytes = 5413698958,
            Note = "原始底模（给训练 LoRA 的人用）——不装 8 步加速，用在 SFT 上的加速 LoRA 别配它",
        },
        new MossFile
        {
            Id = "te-qwen35-int8",
            Title = "Qwen3.5 文本编码器 · INT8 ConvRot",
            Repo = "Comfy-Org/MOSS-Image",
            RepoPath = "text_encoders/qwen3.5_9b_int8_convrot.safetensors",
            Target = MossTarget.TextEncoders,
            Bytes = 9748121532,
            Note = "真正把峰值显存从约 15G 压到约 9G 的是它——只压主模型不省显存",
        },
        new MossFile
        {
            Id = "vae-flux2",
            Title = "FLUX.2 VAE",
            Repo = "Comfy-Org/MOSS-Image",
            RepoPath = "vae/flux2-vae.safetensors",
            Target = MossTarget.Vae,
            Bytes = 336211292,
            Note = "把 latent 变成图片的那一段——任何方案都要",
        },
        new MossFile
        {
            Id = "lora-dmd-8step",
            Title = "8 步加速 LoRA · DMD",
            Repo = "lodestones/MOSS-Image-SFT-8-steps-dmd",
            RepoPath = "lora.safetensors",
            Target = MossTarget.Loras,
            SubDir = "MOSS",
            FileName = "MOSS-SFT-8step-DMD.safetensors",
            Bytes = 1189176800,
            Note = "8 步 / CFG 1 的 DMD 蒸馏 LoRA——只配 SFT（含压缩版），配 Base 会出色块",
        },
        new MossFile
        {
            Id = "hf-transformer-config",
            Title = "官方完整版 · transformer 配置",
            Repo = "OpenMOSS-Team/MOSS-Image-SFT",
            RepoPath = "transformer/config.json",
            Target = MossTarget.HfDirectory,
            SubDir = "MOSS-Image-SFT",
            Bytes = 240,
            Note = "diffusers 目录方案（HF Loader）用",
            ModelScopeRepo = "OpenMOSS/MOSS-Image-SFT",
        },
        new MossFile
        {
            Id = "hf-transformer-1",
            Title = "官方完整版 · transformer 分片 1/3",
            Repo = "OpenMOSS-Team/MOSS-Image-SFT",
            RepoPath = "transformer/diffusion_pytorch_model-00001-of-00003.safetensors",
            Target = MossTarget.HfDirectory,
            SubDir = "MOSS-Image-SFT",
            Bytes = 4991978016,
            Note = "diffusers 目录方案（HF Loader）用",
            ModelScopeRepo = "OpenMOSS/MOSS-Image-SFT",
        },
        new MossFile
        {
            Id = "hf-transformer-2",
            Title = "官方完整版 · transformer 分片 2/3",
            Repo = "OpenMOSS-Team/MOSS-Image-SFT",
            RepoPath = "transformer/diffusion_pytorch_model-00002-of-00003.safetensors",
            Target = MossTarget.HfDirectory,
            SubDir = "MOSS-Image-SFT",
            Bytes = 4983137016,
            Note = "diffusers 目录方案（HF Loader）用",
            ModelScopeRepo = "OpenMOSS/MOSS-Image-SFT",
        },
        new MossFile
        {
            Id = "hf-transformer-3",
            Title = "官方完整版 · transformer 分片 3/3",
            Repo = "OpenMOSS-Team/MOSS-Image-SFT",
            RepoPath = "transformer/diffusion_pytorch_model-00003-of-00003.safetensors",
            Target = MossTarget.HfDirectory,
            SubDir = "MOSS-Image-SFT",
            Bytes = 189549184,
            Note = "diffusers 目录方案（HF Loader）用",
            ModelScopeRepo = "OpenMOSS/MOSS-Image-SFT",
        },
        new MossFile
        {
            Id = "hf-transformer-index",
            Title = "官方完整版 · transformer 分片索引",
            Repo = "OpenMOSS-Team/MOSS-Image-SFT",
            RepoPath = "transformer/diffusion_pytorch_model.safetensors.index.json",
            Target = MossTarget.HfDirectory,
            SubDir = "MOSS-Image-SFT",
            Bytes = 40298,
            Note = "diffusers 目录方案（HF Loader）用",
            ModelScopeRepo = "OpenMOSS/MOSS-Image-SFT",
        },
        new MossFile
        {
            Id = "hf-text-config",
            Title = "官方完整版 · text_encoder 配置",
            Repo = "OpenMOSS-Team/MOSS-Image-SFT",
            RepoPath = "text_encoder/config.json",
            Target = MossTarget.HfDirectory,
            SubDir = "MOSS-Image-SFT",
            Bytes = 2813,
            Note = "diffusers 目录方案（HF Loader）用",
            ModelScopeRepo = "OpenMOSS/MOSS-Image-SFT",
        },
        new MossFile
        {
            Id = "hf-text-1",
            Title = "官方完整版 · text_encoder 分片 1/4",
            Repo = "OpenMOSS-Team/MOSS-Image-SFT",
            RepoPath = "text_encoder/model-00001-of-00004.safetensors",
            Target = MossTarget.HfDirectory,
            SubDir = "MOSS-Image-SFT",
            Bytes = 4973910744,
            Note = "diffusers 目录方案（HF Loader）用",
            ModelScopeRepo = "OpenMOSS/MOSS-Image-SFT",
        },
        new MossFile
        {
            Id = "hf-text-2",
            Title = "官方完整版 · text_encoder 分片 2/4",
            Repo = "OpenMOSS-Team/MOSS-Image-SFT",
            RepoPath = "text_encoder/model-00002-of-00004.safetensors",
            Target = MossTarget.HfDirectory,
            SubDir = "MOSS-Image-SFT",
            Bytes = 4988364288,
            Note = "diffusers 目录方案（HF Loader）用",
            ModelScopeRepo = "OpenMOSS/MOSS-Image-SFT",
        },
        new MossFile
        {
            Id = "hf-text-3",
            Title = "官方完整版 · text_encoder 分片 3/4",
            Repo = "OpenMOSS-Team/MOSS-Image-SFT",
            RepoPath = "text_encoder/model-00003-of-00004.safetensors",
            Target = MossTarget.HfDirectory,
            SubDir = "MOSS-Image-SFT",
            Bytes = 4954202112,
            Note = "diffusers 目录方案（HF Loader）用",
            ModelScopeRepo = "OpenMOSS/MOSS-Image-SFT",
        },
        new MossFile
        {
            Id = "hf-text-4",
            Title = "官方完整版 · text_encoder 分片 4/4",
            Repo = "OpenMOSS-Team/MOSS-Image-SFT",
            RepoPath = "text_encoder/model-00004-of-00004.safetensors",
            Target = MossTarget.HfDirectory,
            SubDir = "MOSS-Image-SFT",
            Bytes = 1869001848,
            Note = "diffusers 目录方案（HF Loader）用",
            ModelScopeRepo = "OpenMOSS/MOSS-Image-SFT",
        },
        new MossFile
        {
            Id = "hf-text-index",
            Title = "官方完整版 · text_encoder 分片索引",
            Repo = "OpenMOSS-Team/MOSS-Image-SFT",
            RepoPath = "text_encoder/model.safetensors.index.json",
            Target = MossTarget.HfDirectory,
            SubDir = "MOSS-Image-SFT",
            Bytes = 64641,
            Note = "diffusers 目录方案（HF Loader）用",
            ModelScopeRepo = "OpenMOSS/MOSS-Image-SFT",
        },
        new MossFile
        {
            Id = "hf-tokenizer",
            Title = "官方完整版 · tokenizer",
            Repo = "OpenMOSS-Team/MOSS-Image-SFT",
            RepoPath = "tokenizer/tokenizer.json",
            Target = MossTarget.HfDirectory,
            SubDir = "MOSS-Image-SFT",
            Bytes = 19989325,
            Note = "diffusers 目录方案（HF Loader）用",
            ModelScopeRepo = "OpenMOSS/MOSS-Image-SFT",
        },
        new MossFile
        {
            Id = "hf-tokenizer-config",
            Title = "官方完整版 · tokenizer 配置",
            Repo = "OpenMOSS-Team/MOSS-Image-SFT",
            RepoPath = "tokenizer/tokenizer_config.json",
            Target = MossTarget.HfDirectory,
            SubDir = "MOSS-Image-SFT",
            Bytes = 1123,
            Note = "diffusers 目录方案（HF Loader）用",
            ModelScopeRepo = "OpenMOSS/MOSS-Image-SFT",
        },
        new MossFile
        {
            Id = "hf-tokenizer-template",
            Title = "官方完整版 · tokenizer 模板",
            Repo = "OpenMOSS-Team/MOSS-Image-SFT",
            RepoPath = "tokenizer/chat_template.jinja",
            Target = MossTarget.HfDirectory,
            SubDir = "MOSS-Image-SFT",
            Bytes = 7756,
            Note = "diffusers 目录方案（HF Loader）用",
            ModelScopeRepo = "OpenMOSS/MOSS-Image-SFT",
        },
        new MossFile
        {
            Id = "hf-vae-config",
            Title = "官方完整版 · VAE 配置",
            Repo = "OpenMOSS-Team/MOSS-Image-SFT",
            RepoPath = "vae/config.json",
            Target = MossTarget.HfDirectory,
            SubDir = "MOSS-Image-SFT",
            Bytes = 838,
            Note = "diffusers 目录方案（HF Loader）用",
            ModelScopeRepo = "OpenMOSS/MOSS-Image-SFT",
        },
        new MossFile
        {
            Id = "hf-vae",
            Title = "官方完整版 · VAE",
            Repo = "OpenMOSS-Team/MOSS-Image-SFT",
            RepoPath = "vae/diffusion_pytorch_model.safetensors",
            Target = MossTarget.HfDirectory,
            SubDir = "MOSS-Image-SFT",
            Bytes = 249521372,
            Note = "diffusers 目录方案（HF Loader）用",
            ModelScopeRepo = "OpenMOSS/MOSS-Image-SFT",
        },
        new MossFile
        {
            Id = "hf-pipeline",
            Title = "官方完整版 · pipeline_moss_image.py",
            Repo = "OpenMOSS-Team/MOSS-Image-SFT",
            RepoPath = "pipeline_moss_image.py",
            Target = MossTarget.HfDirectory,
            SubDir = "MOSS-Image-SFT",
            Bytes = 25060,
            Note = "图像编辑插件按文件路径 import 这一份官方 pipeline——缺它编辑链直接报「不是完整的 snapshot」",
            ModelScopeRepo = "OpenMOSS/MOSS-Image-SFT",
        },
        new MossFile
        {
            Id = "hf-model-index",
            Title = "官方完整版 · model_index.json",
            Repo = "OpenMOSS-Team/MOSS-Image-SFT",
            RepoPath = "model_index.json",
            Target = MossTarget.HfDirectory,
            SubDir = "MOSS-Image-SFT",
            Bytes = 412,
            Note = "diffusers 目录的组件索引（transformer / text_encoder / tokenizer / vae / scheduler）",
            ModelScopeRepo = "OpenMOSS/MOSS-Image-SFT",
        },
        new MossFile
        {
            Id = "hf-configuration",
            Title = "官方完整版 · configuration.json",
            Repo = "OpenMOSS-Team/MOSS-Image-SFT",
            RepoPath = "configuration.json",
            Target = MossTarget.HfDirectory,
            SubDir = "MOSS-Image-SFT",
            Bytes = 71,
            Note = "仓库级配置文件（官方快照自带）",
            ModelScopeRepo = "OpenMOSS/MOSS-Image-SFT",
        },
        new MossFile
        {
            Id = "hf-scheduler-config",
            Title = "官方完整版 · scheduler 配置",
            Repo = "OpenMOSS-Team/MOSS-Image-SFT",
            RepoPath = "scheduler/scheduler_config.json",
            Target = MossTarget.HfDirectory,
            SubDir = "MOSS-Image-SFT",
            Bytes = 494,
            Note = "编辑插件按 snapshot\\scheduler 读 MOSSImageScheduler——缺它采样器建不起来",
            ModelScopeRepo = "OpenMOSS/MOSS-Image-SFT",
        },
        new MossFile
        {
            Id = "edit-adapter-model",
            Title = "图像编辑 LoRA · adapter_model.safetensors",
            Repo = "chinoll/Moss-Image-Edit",
            RepoPath = "adapter_model.safetensors",
            Target = MossTarget.MossEditAdapter,
            FileName = "adapter_model.safetensors",
            Bytes = 297382216,
            Note = "编辑 LoRA 权重（rank 64 / alpha 64 / bf16）——挂在 42 层的 q/k/v/o/ffn 上，约 284M；魔搭无镜像，只能走 hf-mirror",
        },
        new MossFile
        {
            Id = "edit-adapter-config",
            Title = "图像编辑 LoRA · adapter_config.json",
            Repo = "chinoll/Moss-Image-Edit",
            RepoPath = "adapter_config.json",
            Target = MossTarget.MossEditAdapter,
            FileName = "adapter_config.json",
            Bytes = 53712,
            Note = "编辑 LoRA 的模块清单与超参（252 个 target、权重校验和）——插件按它严格校验权重",
        },
    };

    /// <summary>全部方案——推荐方案 + 可选方案。</summary>
    public static readonly IReadOnlyList<MossPlan> Plans = new List<MossPlan>
    {
        new MossPlan
        {
            Id = "int8",
            Title = "压缩版组合（INT8）",
            Note = "主模型 INT8 + 文本编码器 INT8 + Flux.2 VAE——约 15.5G 磁盘，峰值显存约 8.7G",
            Files = new[] { "main-sft-int8", "te-qwen35-int8", "vae-flux2" },
            MinVramMb = 10240,
            Recommended = true,
        },
        new MossPlan
        {
            Id = "bf16",
            Title = "官方精度（bf16 主模型 + INT8 文本编码器）",
            Note = "约 20.3G 磁盘，峰值显存约 15G——18G 以上显存用",
            Files = new[] { "main-sft-bf16", "te-qwen35-int8", "vae-flux2" },
            MinVramMb = 18432,
            Recommended = true,
        },
        new MossPlan
        {
            Id = "base",
            Title = "Base 底模（INT8）",
            Note = "约 15.5G 磁盘——原始底模，给训练 LoRA 的人；不配 8 步加速",
            Files = new[] { "main-base-int8", "te-qwen35-int8", "vae-flux2" },
            MinVramMb = 10240,
            Recommended = false,
        },
        new MossPlan
        {
            Id = "diffusers",
            Title = "官方完整目录（diffusers / HF Loader）",
            Note = "约 27G 磁盘——transformers 分片目录，走 HF Loader；只在下单文件格式不可用时用",
            Files = new[]
            {
                "hf-transformer-config", "hf-transformer-1", "hf-transformer-2", "hf-transformer-3",
                "hf-transformer-index", "hf-text-config", "hf-text-1", "hf-text-2", "hf-text-3",
                "hf-text-4", "hf-text-index", "hf-tokenizer", "hf-tokenizer-config",
                "hf-tokenizer-template", "hf-vae-config", "hf-vae",
                "hf-pipeline", "hf-model-index", "hf-configuration", "hf-scheduler-config",
            },
            MinVramMb = 18432,
            Recommended = false,
        },
        new MossPlan
        {
            Id = "accel",
            Title = "8 步加速 LoRA（DMD）",
            Note = "约 1.2G——把官方 50 步 + CFG 3 缩到 8 步 / CFG 1，快 6~9 倍",
            Files = new[] { "lora-dmd-8step" },
            MinVramMb = 0,
            Recommended = false,
        },
        new MossPlan
        {
            Id = "edit",
            Title = "图像编辑（diffusers 底模 + 编辑 LoRA）",
            Note = "约 27.3G 磁盘——官方 diffusers 全量目录 + chinoll 的编辑 LoRA；"
                + "编辑链只能走这条（参考图以 KV 形式进注意力，单文件主模型没有这条通路）",
            Files = new[]
            {
                "hf-transformer-config", "hf-transformer-1", "hf-transformer-2", "hf-transformer-3",
                "hf-transformer-index", "hf-text-config", "hf-text-1", "hf-text-2", "hf-text-3",
                "hf-text-4", "hf-text-index", "hf-tokenizer", "hf-tokenizer-config",
                "hf-tokenizer-template", "hf-vae-config", "hf-vae",
                "hf-pipeline", "hf-model-index", "hf-configuration", "hf-scheduler-config",
                "edit-adapter-model", "edit-adapter-config",
            },
            MinVramMb = 8192,
            Recommended = false,
        },
    };

    /// <summary>按 id 找索引条目。</summary>
    /// <param name="id">索引 id。</param>
    /// <returns>条目；找不到返回 null。</returns>
    public static MossFile? Find(string id)
    {
        foreach (MossFile file in Files)
        {
            if (string.Equals(file.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }

        return null;
    }

    /// <summary>按 id 找方案。</summary>
    /// <param name="id">方案 id。</param>
    /// <returns>方案；找不到返回 null。</returns>
    public static MossPlan? FindPlan(string id)
    {
        foreach (MossPlan plan in Plans)
        {
            if (string.Equals(plan.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return plan;
            }
        }

        return null;
    }

    /// <summary>按显存推荐方案——18G 以上走官方精度（bf16 主模型），其余一律压缩版组合（int8）。</summary>
    /// <param name="vramMb">显存（MB）；0 = 没测到。</param>
    /// <returns>推荐方案 id。</returns>
    public static string RecommendPlan(int vramMb)
    {
        if (vramMb >= 18432)
        {
            return "bf16";
        }

        return "int8";
    }

    /// <summary>
    /// 按显存推荐显存使用方式（ComfyUI 启动参数口径）。
    /// 2026-10-18 定：**一律「按需放进显卡」（normalvram）**——跑的时候整块显存都是它的，
    /// 压缩版组合峰值约 8.7 G，8 G 卡靠 ComfyUI 自己的换出也能过；逐层搬运只作手动选项（慢得没道理）。
    /// </summary>
    /// <param name="vramMb">显存（MB）；0 = 没测到。</param>
    /// <returns>lowvram（逐层搬运）/ normalvram（按需）/ highvram（全部常驻）。</returns>
    public static string RecommendVramMode(int vramMb)
    {
        if (vramMb >= 30000)
        {
            return "highvram";
        }

        return "normalvram";
    }

    /// <summary>拼出某条目的下载地址。</summary>
    /// <param name="file">索引条目。</param>
    /// <param name="source">下载源标识（hf-mirror / hf / modelscope）。</param>
    /// <returns>地址；该源不可用返回空串。</returns>
    public static string Url(MossFile file, string source)
    {
        if (string.Equals(source, "modelscope", StringComparison.OrdinalIgnoreCase))
        {
            // 魔搭多数镜像与 HF 同名同路径（实测 Comfy-Org/MOSS-Image 与 lodestones 的 LoRA 都有镜像）；
            // 没镜像是 404，下载器会自动落到下一个源。显式 ModelScopeRepo 优先（diffusers 那套仓库名不同）
            string repo = file.ModelScopeRepo.Length > 0 ? file.ModelScopeRepo : file.Repo;
            return "https://modelscope.cn/models/" + repo + "/resolve/master/" + file.RepoPath;
        }

        if (string.Equals(source, "hf", StringComparison.OrdinalIgnoreCase))
        {
            return "https://huggingface.co/" + file.Repo + "/resolve/main/" + file.RepoPath;
        }

        if (string.Equals(source, "hf-mirror", StringComparison.OrdinalIgnoreCase))
        {
            return "https://hf-mirror.com/" + file.Repo + "/resolve/main/" + file.RepoPath;
        }

        return "";
    }

    /// <summary>该条目可用的下载地址（按 SourceOrder 排序，空地址跳过）。</summary>
    /// <param name="file">索引条目。</param>
    /// <returns>地址列表。</returns>
    public static string[] Urls(MossFile file)
    {
        var list = new List<string>();
        foreach (string source in SourceOrder)
        {
            string url = Url(file, source);
            if (url.Length > 0)
            {
                list.Add(url);
            }
        }

        return list.ToArray();
    }

    /// <summary>方案总字节数。</summary>
    /// <param name="plan">方案。</param>
    /// <returns>字节数。</returns>
    public static long PlanBytes(MossPlan plan)
    {
        long total = 0;
        foreach (string id in plan.Files)
        {
            MossFile? file = Find(id);
            if (file is not null)
            {
                total += file.Bytes;
            }
        }

        return total;
    }
}
