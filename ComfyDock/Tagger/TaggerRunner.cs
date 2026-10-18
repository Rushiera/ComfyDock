using System;
using System.Collections.Generic;

namespace ComfyDock.Tagger;

/// <summary>一次打标的全部参数（面板上那一屏，原样发给 worker）。</summary>
public sealed class TaggerGenParams
{
    /// <summary>模型 id（索引内）。</summary>
    public string ModelId { get; set; } = "";

    /// <summary>待打标路径——单个图片文件，或一个目录（目录取一层图片）。</summary>
    public string InputPath { get; set; } = "";

    /// <summary>通用标签阈值（官方默认 0.35）。</summary>
    public double GeneralThreshold { get; set; } = 0.35;

    /// <summary>角色标签阈值（官方默认 0.85）。</summary>
    public double CharacterThreshold { get; set; } = 0.85;

    /// <summary>通用标签改用 MCut 自适应阈值（阈值滑杆随之失效）。</summary>
    public bool GeneralMcut { get; set; }

    /// <summary>角色标签改用 MCut 自适应阈值（下限 0.15）。</summary>
    public bool CharacterMcut { get; set; }

    /// <summary>把结果落成 txt（每图一份，与图片同名的 .txt）。</summary>
    public bool WriteText { get; set; } = true;

    /// <summary>推理设备（cuda / cpu / auto）。</summary>
    public string Device { get; set; } = "auto";
}

/// <summary>单张图的打标结果。</summary>
public sealed class TaggerImageResult
{
    /// <summary>图片文件名（含扩展名）。</summary>
    public string File { get; set; } = "";

    /// <summary>图片完整路径。</summary>
    public string Path { get; set; } = "";

    /// <summary>分级（general / sensitive / questionable / explicit——取概率最高的一项）。</summary>
    public string Rating { get; set; } = "";

    /// <summary>分级各项概率。</summary>
    public List<TaggerTag> Ratings { get; set; } = new List<TaggerTag>();

    /// <summary>命中的通用标签（按概率降序）。</summary>
    public List<TaggerTag> General { get; set; } = new List<TaggerTag>();

    /// <summary>命中的角色标签（按概率降序）。</summary>
    public List<TaggerTag> Characters { get; set; } = new List<TaggerTag>();

    /// <summary>通用标签拼成的提示词串（官方口径：逗号分隔、括号转义）。</summary>
    public string GeneralText { get; set; } = "";

    /// <summary>落盘的 txt 文件名（WriteText=false 时为空）。</summary>
    public string TextFile { get; set; } = "";

    /// <summary>本张耗时（秒）。</summary>
    public double Seconds { get; set; }
}

/// <summary>一个标签 + 概率。</summary>
public sealed class TaggerTag
{
    /// <summary>标签名。</summary>
    public string Name { get; set; } = "";

    /// <summary>概率（0..1）。</summary>
    public double Score { get; set; }
}

/// <summary>一次批量打标任务的状态（面板轮询它）。</summary>
public sealed class TaggerJobStatus
{
    /// <summary>任务号。</summary>
    public string Id { get; set; } = "";

    /// <summary>状态：idle / submitted / running / done / error。</summary>
    public string State { get; set; } = "idle";

    /// <summary>人话说明。</summary>
    public string Message { get; set; } = "";

    /// <summary>本次用的模型名。</summary>
    public string Model { get; set; } = "";

    /// <summary>已处理张数。</summary>
    public int Done { get; set; }

    /// <summary>总张数。</summary>
    public int Total { get; set; }

    /// <summary>当前正在处理的文件名。</summary>
    public string Current { get; set; } = "";

    /// <summary>结果清单（新→旧按处理顺序）。</summary>
    public List<TaggerImageResult> Items { get; set; } = new List<TaggerImageResult>();

    /// <summary>失败项（文件名 + 原因）。</summary>
    public List<string> Failed { get; set; } = new List<string>();

    /// <summary>总耗时（秒）。</summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>提交时刻。</summary>
    public string SubmittedAt { get; set; } = "";

    /// <summary>结束时刻。</summary>
    public string FinishedAt { get; set; } = "";
}
