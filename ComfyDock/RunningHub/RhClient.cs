using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.RunningHub;

/// <summary>线上回传的一个产物文件（节点 → 下载地址）。</summary>
public sealed class RhResultFile
{
    /// <summary>产出它的节点 id。</summary>
    public string NodeId { get; set; } = "";

    /// <summary>下载地址。</summary>
    public string Url { get; set; } = "";

    /// <summary>文件扩展名（服务端回传的 outputType，如 png / mp4 / latent / txt）。</summary>
    public string OutputType { get; set; } = "";
}

/// <summary>一次任务查询的解析结果（失败任务是合法回执，不抛错）。</summary>
public sealed class RhQueryResult
{
    /// <summary>查询本身是否成功（网络 / 回执面）。</summary>
    public bool Ok { get; set; }

    /// <summary>任务状态：QUEUED / RUNNING / SUCCESS / FAILED 等。</summary>
    public string Status { get; set; } = "";

    /// <summary>API 错误码（非空即出错）。</summary>
    public string ErrorCode { get; set; } = "";

    /// <summary>API 错误说明。</summary>
    public string ErrorMessage { get; set; } = "";

    /// <summary>本次任务消耗的 RH 币（服务端回传，本地不估算）。</summary>
    public double ConsumeCoins { get; set; }

    /// <summary>产物清单。</summary>
    public List<RhResultFile> Results { get; set; } = new List<RhResultFile>();
}

/// <summary>一个节点参数覆盖（起任务时 nodeInfoList 的一项）。</summary>
public sealed class RhNodeField
{
    /// <summary>节点 id。</summary>
    public string NodeId { get; set; } = "";

    /// <summary>字段名。</summary>
    public string FieldName { get; set; } = "";

    /// <summary>字段值。</summary>
    public string FieldValue { get; set; } = "";
}

/// <summary>
/// RunningHub 开放 API 客户端——上传 / 起工作流 / 查任务 / 取消 / 下载产物。
/// 工作流 id 与节点映射**写死常量**（2026-10-04 定：这条 workflow 不会换）；
/// 端点契约来源见 `协商知识库/online-asr.md` §七（只读借鉴，不动对方文件）。
/// </summary>
public sealed class RhClient
{
    /// <summary>平台基址。</summary>
    public const string BaseUrl = "https://www.runninghub.cn";

    /// <summary>音频转写工作流 id（`[音频转写]Qwen3ASR1.7B`）。</summary>
    public const string WorkflowId = "2106700867842957314";

    /// <summary>线上生成工作流 id（`[生成]Qwen-AIO-串行处理多次抽卡`）。</summary>
    public const string GenWorkflowId = "2103443517661077506";

    /// <summary>生成工作流的图像节点 id（LoadImage）。</summary>
    public const string GenImageNode = "21";

    /// <summary>生成工作流的图像字段名。</summary>
    public const string GenImageField = "image";

    /// <summary>生成工作流的提示词节点 id（TextEncodeQwenImageEditPlus）。</summary>
    public const string GenPromptNode = "3";

    /// <summary>生成工作流的提示词字段名。</summary>
    public const string GenPromptField = "prompt";

    /// <summary>吃音频的节点 id（LoadAudio）。</summary>
    public const string AudioNode = "4";

    /// <summary>音频节点的字段名。</summary>
    public const string AudioField = "audio";

    /// <summary>文本 txt 的输出节点 id（SaveText）。</summary>
    public const string TextNode = "13";

    /// <summary>时间戳 txt 的输出节点 id（SaveText）。</summary>
    public const string TimeNode = "14";

    /// <summary>上传端点。</summary>
    private const string UploadPath = "/openapi/v2/media/upload/binary";

    /// <summary>起任务端点前缀（后接工作流 id）。</summary>
    private const string RunPath = "/openapi/v2/run/workflow/";

    /// <summary>查任务端点。</summary>
    private const string QueryPath = "/openapi/v2/query";

    /// <summary>取消任务端点（v1 面——body 带 apiKey）。</summary>
    private const string CancelPath = "/task/openapi/cancel";

    /// <summary>共享 HTTP 客户端——超时逐调用用 CancellationToken 控制，故不设全局超时。</summary>
    private static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>上传本地音频——返回服务侧文件名（喂给工作流的音频节点）。</summary>
    /// <param name="filePath">音频完整路径。</param>
    /// <param name="apiKey">账号的消费级密钥。</param>
    /// <param name="timeoutSeconds">超时秒数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>服务侧文件名。</returns>
    public async Task<string> UploadAsync(string filePath, string apiKey, int timeoutSeconds, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            throw new InvalidOperationException("音频不在：" + filePath);
        }

        // [段1] 组 multipart——上传名用 ASCII 短名（内容无关的哈希 + 原扩展名），
        // 中文名进 Content-Disposition 会因编码分歧被服务端读成乱码
        string shortName = ShortNameOf(filePath);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using FileStream stream = File.OpenRead(filePath);
        using StreamContent content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using MultipartFormDataContent form = new MultipartFormDataContent();
        form.Add(content, "file", shortName);

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + UploadPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = form;

        // [段2] 发送并解析回执
        string body = await SendAsync(request, timeout.Token).ConfigureAwait(false);
        using JsonDocument document = ParseBody(body);
        JsonElement root = document.RootElement;
        EnsureNoApiError(root, body);

        JsonElement data;
        if (!root.TryGetProperty("data", out data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("上传回执缺 data：" + Head(body));
        }

        JsonElement fileName;
        if (!data.TryGetProperty("fileName", out fileName) || fileName.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("上传回执缺 fileName：" + Head(body));
        }

        string value = fileName.GetString() ?? "";
        if (value.Length == 0)
        {
            throw new InvalidOperationException("上传回执的 fileName 为空");
        }

        return value;
    }

    /// <summary>发起音频转写任务——返回任务号（只覆盖吃音频那一个节点）。</summary>
    /// <param name="workflowId">云端工作流 id（工作区携带）。</param>
    /// <param name="apiKey">账号的消费级密钥。</param>
    /// <param name="uploadedFileName">上传回执里的服务侧文件名。</param>
    /// <param name="timeoutSeconds">超时秒数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>任务号。</returns>
    public async Task<string> RunAsync(string workflowId, string apiKey, string uploadedFileName, int timeoutSeconds, CancellationToken ct)
    {
        var nodes = new List<RhNodeField>();
        var node = new RhNodeField();
        node.NodeId = AudioNode;
        node.FieldName = AudioField;
        node.FieldValue = uploadedFileName;
        nodes.Add(node);
        return await RunNodesAsync(workflowId, apiKey, nodes, timeoutSeconds, ct).ConfigureAwait(false);
    }

    /// <summary>发起工作流任务——按节点映射列表覆盖参数，返回任务号。</summary>
    /// <param name="workflowId">云端工作流 id（工作区携带）。</param>
    /// <param name="apiKey">账号的消费级密钥。</param>
    /// <param name="nodes">节点参数覆盖清单。</param>
    /// <param name="timeoutSeconds">超时秒数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>任务号。</returns>
    public async Task<string> RunNodesAsync(string workflowId, string apiKey, List<RhNodeField> nodes, int timeoutSeconds, CancellationToken ct)
    {
        // [段1] 组 nodeInfoList
        var list = new List<object>();
        foreach (RhNodeField field in nodes)
        {
            var item = new Dictionary<string, object>();
            item["nodeId"] = field.NodeId;
            item["fieldName"] = field.FieldName;
            item["fieldValue"] = field.FieldValue;
            list.Add(item);
        }

        var payload = new Dictionary<string, object>();
        payload["addMetadata"] = false;
        payload["nodeInfoList"] = list;
        payload["instanceType"] = "default";
        payload["usePersonalQueue"] = "false";

        // [段2] 提交并解析回执——工作流 id 取参数（旧版这里写死常量 WorkflowId，工作区携带的 id 被忽略）
        string json = JsonSerializer.Serialize(payload);
        string body = await PostJsonAsync(BaseUrl + RunPath + workflowId, apiKey, json, timeoutSeconds, ct).ConfigureAwait(false);

        using JsonDocument document = ParseBody(body);
        JsonElement root = document.RootElement;
        EnsureNoApiError(root, body);

        JsonElement taskId;
        if (!root.TryGetProperty("taskId", out taskId) || taskId.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("起任务回执缺 taskId：" + Head(body));
        }

        string value = taskId.GetString() ?? "";
        if (value.Length == 0)
        {
            throw new InvalidOperationException("起任务回执的 taskId 为空");
        }

        return value;
    }

    /// <summary>查任务——失败任务是合法回执，不抛错（由调用方按状态处置）。</summary>
    /// <param name="apiKey">账号的消费级密钥。</param>
    /// <param name="taskId">任务号。</param>
    /// <param name="timeoutSeconds">超时秒数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>查询结果。</returns>
    public async Task<RhQueryResult> QueryAsync(string apiKey, string taskId, int timeoutSeconds, CancellationToken ct)
    {
        var payload = new Dictionary<string, object>();
        payload["taskId"] = taskId;

        string json = JsonSerializer.Serialize(payload);
        string body = await PostJsonAsync(BaseUrl + QueryPath, apiKey, json, timeoutSeconds, ct).ConfigureAwait(false);

        var result = new RhQueryResult();
        using JsonDocument document = ParseBody(body);
        JsonElement root = document.RootElement;

        // [段1] API 层错误码——任务不存在 / 已过期等
        JsonElement errorCode;
        if (root.TryGetProperty("errorCode", out errorCode) && errorCode.ValueKind == JsonValueKind.String)
        {
            result.ErrorCode = errorCode.GetString() ?? "";
        }

        JsonElement errorMessage;
        if (root.TryGetProperty("errorMessage", out errorMessage) && errorMessage.ValueKind == JsonValueKind.String)
        {
            result.ErrorMessage = errorMessage.GetString() ?? "";
        }

        if (result.ErrorCode.Length > 0)
        {
            return result;
        }

        result.Ok = true;

        // [段2] 任务状态与消耗
        JsonElement status;
        if (root.TryGetProperty("status", out status) && status.ValueKind == JsonValueKind.String)
        {
            result.Status = status.GetString() ?? "";
        }

        JsonElement usage;
        if (root.TryGetProperty("usage", out usage) && usage.ValueKind == JsonValueKind.Object)
        {
            JsonElement coins;
            if (usage.TryGetProperty("consumeCoins", out coins) && coins.ValueKind == JsonValueKind.Number)
            {
                result.ConsumeCoins = coins.GetDouble();
            }
        }

        // [段3] 产物清单——地址字段名以 url 为准，兼容 fileUrl
        JsonElement results;
        if (root.TryGetProperty("results", out results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in results.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var file = new RhResultFile();
                JsonElement nodeId;
                if (item.TryGetProperty("nodeId", out nodeId) && nodeId.ValueKind == JsonValueKind.String)
                {
                    file.NodeId = nodeId.GetString() ?? "";
                }

                JsonElement outputType;
                if (item.TryGetProperty("outputType", out outputType) && outputType.ValueKind == JsonValueKind.String)
                {
                    file.OutputType = outputType.GetString() ?? "";
                }

                JsonElement url;
                if (item.TryGetProperty("url", out url) && url.ValueKind == JsonValueKind.String)
                {
                    file.Url = url.GetString() ?? "";
                }
                else if (item.TryGetProperty("fileUrl", out url) && url.ValueKind == JsonValueKind.String)
                {
                    file.Url = url.GetString() ?? "";
                }

                if (file.Url.Length > 0)
                {
                    result.Results.Add(file);
                }
            }
        }

        return result;
    }

    /// <summary>取消任务（v1 面——body 带 apiKey；取消不产生 RH 币消耗）。</summary>
    /// <param name="apiKey">账号的消费级密钥。</param>
    /// <param name="taskId">任务号。</param>
    /// <param name="timeoutSeconds">超时秒数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public async Task CancelAsync(string apiKey, string taskId, int timeoutSeconds, CancellationToken ct)
    {
        var payload = new Dictionary<string, object>();
        payload["apiKey"] = apiKey;
        payload["taskId"] = taskId;

        string json = JsonSerializer.Serialize(payload);
        string body = await PostJsonAsync(BaseUrl + CancelPath, apiKey, json, timeoutSeconds, ct).ConfigureAwait(false);

        using JsonDocument document = ParseBody(body);
        EnsureNoApiError(document.RootElement, body);
    }

    /// <summary>下载一个产物文件——原子落位（`.part` → 改名），不出现半截 txt。</summary>
    /// <param name="url">下载地址。</param>
    /// <param name="destPath">目标完整路径。</param>
    /// <param name="timeoutSeconds">超时秒数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>落盘字节数。</returns>
    public async Task<long> DownloadAsync(string url, string destPath, int timeoutSeconds, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string temp = destPath + ".part";
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        long total = 0;
        try
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "ComfyDock/1.0");
            using HttpResponseMessage response = await Http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException("下载失败 HTTP " + (int)response.StatusCode + "：" + url);
            }

            using Stream source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using FileStream target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[65536];
            while (true)
            {
                int read = await source.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await target.WriteAsync(buffer, 0, read, timeout.Token).ConfigureAwait(false);
                total = total + read;
            }

            target.Flush();
        }
        catch (Exception)
        {
            TryDelete(temp);
            throw;
        }

        File.Move(temp, destPath, true);
        return total;
    }

    /// <summary>上传名短名——文件名哈希前 8 位 + 原扩展名（ASCII 安全）。</summary>
    /// <param name="filePath">音频完整路径。</param>
    /// <returns>短名。</returns>
    private static string ShortNameOf(string filePath)
    {
        string name = Path.GetFileName(filePath);
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(name));
        string hex = Convert.ToHexString(hash).ToLowerInvariant();
        string suffix = Path.GetExtension(filePath);
        if (suffix.Length == 0)
        {
            suffix = ".bin";
        }

        return hex.Substring(0, 8) + suffix.ToLowerInvariant();
    }

    /// <summary>POST 一个 JSON 请求——返回回执正文。</summary>
    /// <param name="url">完整地址。</param>
    /// <param name="apiKey">账号密钥。</param>
    /// <param name="json">请求体 JSON。</param>
    /// <param name="timeoutSeconds">超时秒数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>回执正文。</returns>
    private static async Task<string> PostJsonAsync(string url, string apiKey, string json, int timeoutSeconds, CancellationToken ct)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await SendAsync(request, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>发一个请求并取回正文——网络失败一律出声（不静默回落）。</summary>
    /// <param name="request">已组好的请求。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>回执正文。</returns>
    private static async Task<string> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException("HTTP " + (int)response.StatusCode + "：" + Head(body));
            }

            return body;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("请求超时：" + request.RequestUri);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("请求失败：" + request.RequestUri + " · " + ex.Message);
        }
    }

    /// <summary>解析回执 JSON——非 JSON 出声（带正文摘要）。</summary>
    /// <param name="body">回执正文。</param>
    /// <returns>JSON 文档（调用方负责释放）。</returns>
    private static JsonDocument ParseBody(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("回执不是 JSON：" + Head(body) + " · " + ex.Message);
        }
    }

    /// <summary>检查 API 层错误码（errorCode 非空 / code 非 0 即抛）。</summary>
    /// <param name="root">回执根元素。</param>
    /// <param name="body">回执正文（报错时给摘要）。</param>
    private static void EnsureNoApiError(JsonElement root, string body)
    {
        JsonElement errorCode;
        if (root.TryGetProperty("errorCode", out errorCode) && errorCode.ValueKind == JsonValueKind.String)
        {
            string code = errorCode.GetString() ?? "";
            if (code.Length > 0)
            {
                throw new InvalidOperationException("API 报错 " + code + "：" + Head(body));
            }
        }

        JsonElement codeElement;
        if (root.TryGetProperty("code", out codeElement) && codeElement.ValueKind == JsonValueKind.Number)
        {
            int code = codeElement.GetInt32();
            if (code != 0)
            {
                throw new InvalidOperationException("API 报错 " + code + "：" + Head(body));
            }
        }
    }

    /// <summary>正文摘要（前 300 字，报错信息用）。</summary>
    /// <param name="body">正文。</param>
    /// <returns>摘要。</returns>
    private static string Head(string body)
    {
        if (body.Length <= 300)
        {
            return body;
        }

        return body.Substring(0, 300);
    }

    /// <summary>删临时文件——失败不掩盖原始异常。</summary>
    /// <param name="path">文件路径。</param>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            // 临时文件删不掉不影响主流程——出声但不掩盖原始异常
            Console.WriteLine("[RH] 临时文件删不掉：" + path + " · " + ex.Message);
        }
    }
}
