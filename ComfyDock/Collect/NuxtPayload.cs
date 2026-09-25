using System;
using System.Text.Json;

namespace ComfyDock.Collect;

/// <summary>
/// Nuxt 3 SSR payload（<c>__NUXT_DATA__</c>）解析——索引引用数组的解引用通道。
/// </summary>
public static class NuxtPayload
{
    private const string ScriptMarker = "id=\"__NUXT_DATA__\"";

    /// <summary>从 HTML 中截取 __NUXT_DATA__ 脚本块的 JSON 文本。</summary>
    /// <param name="html">页面 HTML。</param>
    /// <returns>JSON 文本；未命中返回 null。</returns>
    public static string? ExtractJson(string html)
    {
        int marker = html.IndexOf(ScriptMarker, StringComparison.Ordinal);
        if (marker < 0)
        {
            return null;
        }

        int start = html.IndexOf('>', marker);
        if (start < 0)
        {
            return null;
        }

        int end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        if (end < 0)
        {
            return null;
        }

        return html.Substring(start + 1, end - start - 1);
    }
}

/// <summary>
/// payload 文档——顶层数组 + 索引引用解引用。
/// </summary>
public sealed class NuxtDocument
{
    private readonly JsonElement[] _nodes;

    private NuxtDocument(JsonElement[] nodes)
    {
        _nodes = nodes;
    }

    /// <summary>解析 payload JSON 文本。</summary>
    /// <param name="json">__NUXT_DATA__ 内容。</param>
    /// <returns>文档对象；解析失败返回 null。</returns>
    public static NuxtDocument? Parse(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var nodes = new JsonElement[doc.RootElement.GetArrayLength()];
            int i = 0;
            foreach (JsonElement node in doc.RootElement.EnumerateArray())
            {
                nodes[i++] = node.Clone();
            }

            return new NuxtDocument(nodes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>取根节点（payload 数组第 2 元素：Nuxt 应用根对象）。</summary>
    /// <returns>根对象；越界返回 null。</returns>
    public JsonElement? Root()
    {
        return ResolveIndex(1);
    }

    /// <summary>沿属性名链逐层解引用取值。</summary>
    /// <param name="start">起始节点。</param>
    /// <param name="path">属性名链（如 state → $srh-login-user-info → totalCoin）。</param>
    /// <returns>解引用后的值；链路断裂返回 null。</returns>
    public JsonElement? Walk(JsonElement start, params string[] path)
    {
        JsonElement? current = start;
        foreach (string name in path)
        {
            if (current is null || current.Value.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!current.Value.TryGetProperty(name, out JsonElement raw))
            {
                return null;
            }

            current = Resolve(raw);
        }

        return current;
    }

    /// <summary>解引用单个节点：整数 → 指向的节点；Reactive 包装 → 内层节点；其余原样返回。</summary>
    /// <param name="node">原始节点。</param>
    /// <returns>解引用后的节点。</returns>
    public JsonElement? Resolve(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out int index))
        {
            return ResolveIndex(index);
        }

        if (node.ValueKind == JsonValueKind.Array && IsWrapper(node, out int wrapped))
        {
            return ResolveIndex(wrapped);
        }

        return node;
    }

    /// <summary>把节点读成字符串（数字节点转文本）。</summary>
    /// <param name="node">节点。</param>
    /// <returns>字符串；非标量返回空串。</returns>
    public static string AsText(JsonElement? node)
    {
        if (node is null)
        {
            return "";
        }

        return node.Value.ValueKind switch
        {
            JsonValueKind.String => node.Value.GetString() ?? "",
            JsonValueKind.Number => node.Value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
    }

    /// <summary>把节点读成整数（字符串数字亦可）。</summary>
    /// <param name="node">节点。</param>
    /// <returns>整数值；无法转换返回 null。</returns>
    public static long? AsNumber(JsonElement? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node.Value.ValueKind == JsonValueKind.Number && node.Value.TryGetInt64(out long direct))
        {
            return direct;
        }

        if (node.Value.ValueKind == JsonValueKind.String && long.TryParse(node.Value.GetString(), out long parsed))
        {
            return parsed;
        }

        return null;
    }

    private JsonElement? ResolveIndex(int index)
    {
        if (index < 0 || index >= _nodes.Length)
        {
            return null;
        }

        JsonElement node = _nodes[index];
        if (node.ValueKind == JsonValueKind.Array && IsWrapper(node, out int wrapped))
        {
            return ResolveIndex(wrapped);
        }

        return node;
    }

    private static bool IsWrapper(JsonElement node, out int inner)
    {
        inner = -1;
        if (node.GetArrayLength() != 2)
        {
            return false;
        }

        JsonElement head = node[0];
        if (head.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string tag = head.GetString() ?? "";
        if (tag != "Reactive" && tag != "ShallowReactive" && tag != "Ref")
        {
            return false;
        }

        return node[1].TryGetInt32(out inner);
    }
}
