using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ComfyDock.RunningHub;

/// <summary>
/// 一个线上识别工作区——**一条云端工作流对应一块**（面板上一个折叠区）。
/// 音频源与任务按工作区独立；加新工作流 = 加一条工作区，代码不动。
/// </summary>
public sealed class RhWorkspace
{
    /// <summary>工作区 id（内置那块 = asr）。</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名（如 QWEN3ASR1.7B）。</summary>
    public string Name { get; set; } = "";

    /// <summary>云端工作流 id。</summary>
    public string WorkflowId { get; set; } = "";

    /// <summary>该工作区选中的单个音频文件（空 = 未选）。</summary>
    public string AudioFile { get; set; } = "";

    /// <summary>该工作区选中的音频目录（空 = 未选）。</summary>
    public string AudioDir { get; set; } = "";

    /// <summary>该工作区的目录监听自动推——默认关。</summary>
    public bool AutoPush { get; set; }

    /// <summary>工作区类型——KindAsr（音频转写）/ KindGen（图 + 文生成）。</summary>
    public string Kind { get; set; } = KindAsr;

    /// <summary>gen 工作区选中的图片完整路径（空 = 未选）。</summary>
    public string ImageFile { get; set; } = "";

    /// <summary>gen 工作区的提示词。</summary>
    public string Prompt { get; set; } = "";

    /// <summary>是否内置块（内置块不可删）.</summary>
    public bool BuiltIn { get; set; }

    /// <summary>工作区类型取值——音频转写。</summary>
    public const string KindAsr = "asr";

    /// <summary>工作区类型取值——图 + 文生成。</summary>
    public const string KindGen = "gen";
}

/// <summary>
/// 线上识别配置——工作区清单 + 当前活跃工作区（落盘 `data/rh-config.json`）。
/// 音频源从「全局一份」下沉到每个工作区各一份（2026-10-06）。
/// </summary>
public sealed class RhConfig
{
    /// <summary>工作区清单（第一条为内置 QWEN3ASR1.7B）。</summary>
    public List<RhWorkspace> Workspaces { get; set; } = new List<RhWorkspace>();

    /// <summary>当前活跃工作区 id（空 = 取第一条）。</summary>
    public string ActiveWorkspaceId { get; set; } = "";
}

/// <summary>
/// 配置读写——文件缺失即默认值（带内置工作区种子）；解析失败抛错（由调用方出声，不静默回落）。
/// </summary>
public sealed class RhConfigStore
{
    /// <summary>JSON 序列化选项（缩进 + 驼峰 + 中文直出）。</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>配置文件完整路径。</summary>
    private readonly string _filePath;

    /// <summary>以配置文件路径构造。</summary>
    /// <param name="filePath">rh-config.json 完整路径。</param>
    public RhConfigStore(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>配置文件完整路径。</summary>
    public string FilePath
    {
        get { return _filePath; }
    }

    /// <summary>读配置——文件缺失返回带种子的默认值；内容损坏抛错。</summary>
    /// <returns>配置对象。</returns>
    public RhConfig Load()
    {
        RhConfig config;
        if (File.Exists(_filePath))
        {
            string json = File.ReadAllText(_filePath);
            RhConfig? parsed = JsonSerializer.Deserialize<RhConfig>(json, SerializerOptions);
            if (parsed is null)
            {
                throw new InvalidOperationException("配置文件形态不对：" + _filePath);
            }

            if (parsed.Workspaces is null || parsed.Workspaces.Count == 0)
            {
                // 旧形态（音频源是全局一份）——搬到内置块里，不静默丢掉已选目录
                RhWorkspace seed = SeedWorkspace();
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                seed.AudioFile = LegacyString(root, "audioFile");
                seed.AudioDir = LegacyString(root, "audioDir");
                JsonElement autoPush;
                if (root.TryGetProperty("autoPush", out autoPush) && autoPush.ValueKind == JsonValueKind.True)
                {
                    seed.AutoPush = true;
                }

                parsed.Workspaces = new List<RhWorkspace> { seed };
            }

            config = parsed;
        }
        else
        {
            config = new RhConfig();
        }

        if (config.Workspaces is null)
        {
            config.Workspaces = new List<RhWorkspace>();
        }

        // 缺种子即补——两个内置块永远在位；生成块排在音频转写块之前（面板第一块）
        EnsureSeed(config, SeedGenWorkspace());
        EnsureSeed(config, SeedWorkspace());
        OrderBuiltIn(config);

        if (config.ActiveWorkspaceId is null)
        {
            config.ActiveWorkspaceId = "";
        }

        return config;
    }

    /// <summary>取旧形态里的字符串字段（缺失即空串）。</summary>
    /// <param name="root">旧配置根节点。</param>
    /// <param name="name">字段名。</param>
    /// <returns>字段值；缺失或不是字符串即空串。</returns>
    private static string LegacyString(JsonElement root, string name)
    {
        JsonElement value;
        if (root.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? "";
        }

        return "";
    }

    /// <summary>写配置（原子替换——先落 .tmp 再改名，读者不会读到半截）。</summary>
    /// <param name="config">待落盘配置。</param>
    public void Save(RhConfig config)
    {
        string? dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string temp = _filePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, SerializerOptions), Encoding.UTF8);
        File.Move(temp, _filePath, true);
    }

    /// <summary>内置工作区种子——QWEN3ASR1.7B（工作流 id 取客户端常量）。</summary>
    /// <returns>种子工作区。</returns>
    public static RhWorkspace SeedWorkspace()
    {
        var workspace = new RhWorkspace();
        workspace.Id = "asr";
        workspace.Name = "QWEN3ASR1.7B";
        workspace.WorkflowId = RhClient.WorkflowId;
        workspace.Kind = RhWorkspace.KindAsr;
        workspace.BuiltIn = true;
        return workspace;
    }

    /// <summary>内置工作区种子——线上生成（图 + 文 → latent → 内置出图）。</summary>
    /// <returns>种子工作区。</returns>
    public static RhWorkspace SeedGenWorkspace()
    {
        var workspace = new RhWorkspace();
        workspace.Id = "gen";
        workspace.Name = "[生成]Qwen-AIO-串行处理多次抽卡";
        workspace.WorkflowId = RhClient.GenWorkflowId;
        workspace.Kind = RhWorkspace.KindGen;
        workspace.BuiltIn = true;
        return workspace;
    }

    /// <summary>缺哪个内置块就补哪个（已有的保留用户改过的字段，只补空的类型）。</summary>
    /// <param name="config">配置对象。</param>
    /// <param name="seed">种子工作区。</param>
    private static void EnsureSeed(RhConfig config, RhWorkspace seed)
    {
        foreach (RhWorkspace workspace in config.Workspaces)
        {
            if (string.Equals(workspace.Id, seed.Id, StringComparison.OrdinalIgnoreCase))
            {
                if (workspace.Kind.Length == 0)
                {
                    workspace.Kind = seed.Kind;
                }

                return;
            }
        }

        config.Workspaces.Add(seed);
    }

    /// <summary>把生成块挪到最前——面板第一块是生成，第二块是音频转写。</summary>
    /// <param name="config">配置对象。</param>
    private static void OrderBuiltIn(RhConfig config)
    {
        int index = -1;
        for (int i = 0; i < config.Workspaces.Count; i = i + 1)
        {
            if (string.Equals(config.Workspaces[i].Id, "gen", StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index <= 0)
        {
            return;
        }

        RhWorkspace gen = config.Workspaces[index];
        config.Workspaces.RemoveAt(index);
        config.Workspaces.Insert(0, gen);
    }
}
