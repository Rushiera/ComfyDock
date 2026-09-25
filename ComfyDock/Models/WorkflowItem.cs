namespace ComfyDock.Models;

/// <summary>
/// 账号下的一个工作流（来自 /api/workflow/page/simpleList）。
/// </summary>
public sealed class WorkflowItem
{
    /// <summary>工作流 id——跳转与删除都用它。</summary>
    public string WorkflowId { get; set; } = "";

    /// <summary>工作流名称。</summary>
    public string Name { get; set; } = "";

    /// <summary>最后更新时间。</summary>
    public string LastUpdateTime { get; set; } = "";

    /// <summary>节点数。</summary>
    public string NodeCount { get; set; } = "";

    /// <summary>是否置顶。</summary>
    public bool TopFlag { get; set; }

    /// <summary>编辑页地址（https://www.runninghub.cn/workflow/{id}）。</summary>
    public string EditUrl { get; set; } = "";
}
