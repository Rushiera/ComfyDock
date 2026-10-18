using System;
using System.Collections.Generic;

namespace ComfyDock.Tagger;

/// <summary>一条模型文件——索引的最小单位：从哪个仓库的哪个路径来、多大、落盘叫什么。</summary>
public sealed class TaggerFile
{
    /// <summary>索引内唯一 id（下载队列与面板都用它）。</summary>
    public string Id { get; init; } = "";

    /// <summary>面板显示名。</summary>
    public string Title { get; init; } = "";

    /// <summary>所属模型 id。</summary>
    public string ModelId { get; init; } = "";

    /// <summary>Hugging Face 仓库（如 SmilingWolf/wd-eva02-large-tagger-v3）。</summary>
    public string Repo { get; init; } = "";

    /// <summary>仓库内相对路径（model.onnx / selected_tags.csv）。</summary>
    public string RepoPath { get; init; } = "";

    /// <summary>落盘文件名。</summary>
    public string FileName { get; init; } = "";

    /// <summary>字节数（0 = 未实测——完成判据退化为「Content-Length 到齐且文件非空」）。</summary>
    public long Bytes { get; init; }

    /// <summary>用途说明（面板原样显示）。</summary>
    public string Note { get; init; } = "";
}

/// <summary>
/// 一个打标模型——onnx 权重 + 标签表两件套。
/// <para>模型仍住本机 ComfyUI 的模型目录（<c>models\onnx\wd-tagger\&lt;id&gt;\</c>），软件只带索引与下载方式。</para>
/// </summary>
public sealed class TaggerModel
{
    /// <summary>模型 id（面板选择与配置里存的值）。</summary>
    public string Id { get; init; } = "";

    /// <summary>模型名。</summary>
    public string Title { get; init; } = "";

    /// <summary>HF 仓库。</summary>
    public string Repo { get; init; } = "";

    /// <summary>说明（骨干 / 参数量 / 体积口径）。</summary>
    public string Note { get; init; } = "";

    /// <summary>该模型需要的文件（onnx + csv 两条）。</summary>
    public IReadOnlyList<TaggerFile> Files { get; init; } = Array.Empty<TaggerFile>();

    /// <summary>权重文件（model.onnx）——不在位就没法打标。</summary>
    /// <returns>索引条目；缺失返回 null。</returns>
    public TaggerFile? Onnx()
    {
        foreach (TaggerFile file in Files)
        {
            if (file.FileName.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }

        return null;
    }

    /// <summary>标签表（selected_tags.csv）——决定输出标签词表。</summary>
    /// <returns>索引条目；缺失返回 null。</returns>
    public TaggerFile? Labels()
    {
        foreach (TaggerFile file in Files)
        {
            if (file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }

        return null;
    }

    /// <summary>两件合计字节数。</summary>
    /// <returns>字节数。</returns>
    public long TotalBytes()
    {
        long total = 0;
        foreach (TaggerFile file in Files)
        {
            total += file.Bytes;
        }

        return total;
    }
}

/// <summary>
/// WD Tagger 依赖索引——软件内自带的唯一真相：需要哪些文件、从哪下、放哪里。
/// <para>唯一条目（2026-10-18 hf-mirror 实测字节）：<c>SmilingWolf/wd-eva02-large-tagger-v3</c>
/// —— model.onnx 1260435999 B、selected_tags.csv 308468 B（数据集 v3，0.3B 参数）。</para>
/// <para>魔搭没有这个仓库的镜像（实测 <c>modelscope.cn/models/SmilingWolf/wd-eva02-large-tagger-v3</c> 回 404）——
/// 源顺序按 hf-mirror → huggingface → 魔搭，镜像失败自动落下一个。</para>
/// </summary>
public static class TaggerCatalog
{
    /// <summary>下载源顺序——hf-mirror 优先（本机 huggingface.co 直连不可达），魔搭殿后（无镜像时 404）。</summary>
    public static readonly string[] SourceOrder = { "hf-mirror", "hf", "modelscope" };

    /// <summary>ComfyUI 模型目录下的落点子目录（<c>models\onnx\wd-tagger</c>）。</summary>
    public const string SubDir = "wd-tagger";

    /// <summary>唯一模型 id。</summary>
    public const string ModelId = "eva02-large";

    /// <summary>全部模型——只此一套。</summary>
    public static readonly IReadOnlyList<TaggerModel> Models = new List<TaggerModel>
    {
        Make(
            ModelId,
            "WD EVA02-Large Tagger v3",
            "SmilingWolf/wd-eva02-large-tagger-v3",
            "0.3B 参数 · 数据集 v3 · ONNX 前向",
            1260435999L,
            308468L),
    };

    /// <summary>造一个模型条目（onnx + csv 两件）。</summary>
    /// <param name="id">模型 id。</param>
    /// <param name="title">显示名。</param>
    /// <param name="repo">HF 仓库。</param>
    /// <param name="note">说明。</param>
    /// <param name="onnxBytes">onnx 字节数（0 = 未实测）。</param>
    /// <param name="csvBytes">csv 字节数（0 = 未实测）。</param>
    /// <returns>模型条目。</returns>
    private static TaggerModel Make(
        string id,
        string title,
        string repo,
        string note,
        long onnxBytes,
        long csvBytes)
    {
        return new TaggerModel
        {
            Id = id,
            Title = title,
            Repo = repo,
            Note = note,
            Files = new List<TaggerFile>
            {
                new TaggerFile
                {
                    Id = id + ":onnx",
                    Title = title + " · 权重",
                    ModelId = id,
                    Repo = repo,
                    RepoPath = "model.onnx",
                    FileName = "model.onnx",
                    Bytes = onnxBytes,
                    Note = "onnxruntime 直接读的推理图（输入 BGR 方形图，输出全词表概率）",
                },
                new TaggerFile
                {
                    Id = id + ":labels",
                    Title = title + " · 标签表",
                    ModelId = id,
                    Repo = repo,
                    RepoPath = "selected_tags.csv",
                    FileName = "selected_tags.csv",
                    Bytes = csvBytes,
                    Note = "category 0 = 通用标签 / 4 = 角色 / 9 = 分级",
                },
            },
        };
    }

    /// <summary>唯一模型（配置为空时也取它）。</summary>
    /// <returns>模型；索引为空返回 null。</returns>
    public static TaggerModel? DefaultModel()
    {
        return Models.Count > 0 ? Models[0] : null;
    }

    /// <summary>按文件 id 找索引条目。</summary>
    /// <param name="id">文件 id（如 eva02-large:onnx）。</param>
    /// <returns>条目；没有返回 null。</returns>
    public static TaggerFile? FindFile(string id)
    {
        foreach (TaggerModel model in Models)
        {
            foreach (TaggerFile file in model.Files)
            {
                if (string.Equals(file.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
        }

        return null;
    }

    /// <summary>某条目在指定源下的下载地址。</summary>
    /// <param name="file">索引条目。</param>
    /// <param name="source">源名（hf-mirror / hf / modelscope）。</param>
    /// <returns>地址；未知源返回空串。</returns>
    public static string Url(TaggerFile file, string source)
    {
        if (string.Equals(source, "modelscope", StringComparison.OrdinalIgnoreCase))
        {
            return "https://modelscope.cn/models/" + file.Repo + "/resolve/master/" + file.RepoPath;
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

    /// <summary>某条目的全部可用地址（按 SourceOrder 排序）。</summary>
    /// <param name="file">索引条目。</param>
    /// <returns>地址列表。</returns>
    public static string[] Urls(TaggerFile file)
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
}
