using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Collect;

/// <summary>
/// 工作流写操作——上传与删除。全程在套壳浏览器的页面内完成（同源 + 浏览器自带的登录凭据），
/// **凭据不出浏览器**：不读 token 到本进程、不落盘 token。
/// 上传需要给文件框塞本地文件，页面 JS 做不到，走 DevTools 的 DOM.setFileInputFiles。
/// </summary>
public sealed class WorkflowAdmin
{
    /// <summary>删除工作流的接口。</summary>
    private const string RemoveApi = "/api/workflow/remove";

    /// <summary>上传工作流的接口（只用于认捕获结果）。</summary>
    private const string UploadApiPath = "/api/workflow/upload";

    /// <summary>文档创建前注入的 XHR 钩子——把 /api/ 响应收进 window.__rhCapture。</summary>
    private const string HookScript = @"(function(){
  if (window.__rhCapture) { return; }
  window.__rhCapture = [];
  var open = XMLHttpRequest.prototype.open;
  var send = XMLHttpRequest.prototype.send;
  XMLHttpRequest.prototype.open = function(method, url){
    this.__rhMethod = method;
    this.__rhUrl = String(url);
    return open.apply(this, arguments);
  };
  XMLHttpRequest.prototype.send = function(){
    var self = this;
    this.addEventListener('load', function(){
      try {
        if (self.__rhUrl && self.__rhUrl.indexOf('/api/') >= 0) {
          window.__rhCapture.push({
            method: self.__rhMethod,
            url: self.__rhUrl,
            status: self.status,
            body: String(self.responseText || '')
          });
        }
      } catch (e) {}
    });
    return send.apply(this, arguments);
  };
})();";

    /// <summary>套壳浏览器客户端。</summary>
    private readonly ShellClient _shell;

    /// <summary>工作台地址。</summary>
    private readonly string _workspaceUrl;

    /// <summary>构造工作流写操作器。</summary>
    /// <param name="shell">套壳浏览器客户端。</param>
    /// <param name="workspaceUrl">工作台地址。</param>
    public WorkflowAdmin(ShellClient shell, string workspaceUrl)
    {
        _shell = shell;
        _workspaceUrl = workspaceUrl;
    }

    /// <summary>删除一个工作流（幂等语义由站点决定；本方法只报站点返回）。</summary>
    /// <param name="accountId">账号 id。</param>
    /// <param name="workflowId">工作流 id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>操作结果。</returns>
    public async Task<WorkflowOpResult> DeleteAsync(string accountId, string workflowId, CancellationToken ct)
    {
        var result = new WorkflowOpResult();

        // [段1] 后台打开工作台（不弹窗口）
        await _shell.OpenAsync(accountId, _workspaceUrl, false, 90000).ConfigureAwait(false);

        // [段2] 页面内执行删除并等结果
        string output = await RunAsyncScriptAsync(accountId, BuildRemoveBody(workflowId), 30, ct).ConfigureAwait(false);
        result.Response = output;
        result.Message = ReadMessage(output);
        result.Ok = output.Contains("\"code\":0");
        if (!result.Ok && result.Message.Length == 0)
        {
            result.Error = output.Length == 0 ? "站点没有返回结果（页面可能未就绪）" : "站点未返回可识别结果：" + Trim(output);
        }

        return result;
    }

    /// <summary>上传一个工作流文件（给页面里的文件框塞本地文件，由浏览器自己发上传请求）。</summary>
    /// <param name="accountId">账号 id。</param>
    /// <param name="jsonPath">工作流 JSON 完整路径。</param>
    /// <param name="waitSeconds">等待上传接口响应的秒数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>操作结果。</returns>
    public async Task<WorkflowOpResult> UploadAsync(string accountId, string jsonPath, int waitSeconds, CancellationToken ct)
    {
        var result = new WorkflowOpResult();

        if (!File.Exists(jsonPath))
        {
            result.Error = "工作流文件不存在：" + jsonPath;
            return result;
        }

        // [段1] 装钩子并后台打开工作台
        await _shell.AddInitScriptAsync(accountId, HookScript).ConfigureAwait(false);
        await _shell.OpenAsync(accountId, _workspaceUrl, false, 90000).ConfigureAwait(false);

        // [段2] 等文件输入框渲染出来
        int nodeId = 0;
        for (int i = 0; i < 30 && nodeId == 0; i++)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            nodeId = await QueryFileInputAsync(accountId).ConfigureAwait(false);
        }

        if (nodeId == 0)
        {
            result.Error = "页面上没找到文件输入框（导入入口可能已改版）";
            return result;
        }

        // [段3] 塞文件
        string escapedPath = jsonPath.Replace("\\", "\\\\");
        string parameters = "{\"files\":[\"" + escapedPath + "\"],\"nodeId\":" + nodeId + "}";
        string cdpResult = await _shell.CallDevToolsTextAsync(accountId, "DOM.setFileInputFiles", parameters, 30000).ConfigureAwait(false);
        if (cdpResult.Length == 0)
        {
            result.Error = "给文件框塞文件失败";
            return result;
        }

        // [段4] 等上传接口的响应
        for (int i = 0; i < waitSeconds; i++)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            string raw = await _shell.EvalTextAsync(accountId, "JSON.stringify(window.__rhCapture||[])", 20000).ConfigureAwait(false);
            if (raw.Length <= 4)
            {
                continue;
            }

            string body = FindCapture(raw, UploadApiPath);
            if (body.Length > 0)
            {
                result.Ok = true;
                result.Response = body;
                result.Message = ReadMessage(body);
                return result;
            }
        }

        result.Error = "已挂上文件，但没等到上传接口响应（" + waitSeconds + " s）";
        return result;
    }

    // [段1] 页面内异步脚本 + 轮询取结果
    private async Task<string> RunAsyncScriptAsync(string accountId, string body, int waitSeconds, CancellationToken ct)
    {
        string script = "window.__rhResult=null;"
            + "(async function(){try{" + body + "}catch(e){window.__rhResult='ERR:'+e.message;}})();"
            + "'started'";
        await _shell.EvalTextAsync(accountId, script, 20000).ConfigureAwait(false);

        int rounds = waitSeconds * 2;
        for (int i = 0; i < rounds; i++)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            string value = await _shell.EvalTextAsync(accountId, "window.__rhResult||''", 20000).ConfigureAwait(false);
            if (value.Length > 0)
            {
                return value;
            }
        }

        return "";
    }

    private async Task<int> QueryFileInputAsync(string accountId)
    {
        string document = await _shell.CallDevToolsTextAsync(accountId, "DOM.getDocument", "{\"depth\":1}", 20000).ConfigureAwait(false);
        int rootId = ReadNodeId(document, "");
        if (rootId == 0)
        {
            return 0;
        }

        string parameters = "{\"nodeId\":" + rootId + ",\"selector\":\"input[type=file]\"}";
        string found = await _shell.CallDevToolsTextAsync(accountId, "DOM.querySelector", parameters, 20000).ConfigureAwait(false);
        return ReadNodeId(found, "nodeId");
    }

    private static int ReadNodeId(string json, string field)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return 0;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (field.Length == 0)
            {
                JsonElement rootNode;
                if (root.TryGetProperty("root", out rootNode) && rootNode.ValueKind == JsonValueKind.Object)
                {
                    return ReadInt(rootNode, "nodeId");
                }

                return 0;
            }

            return ReadInt(root, field);
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static int ReadInt(JsonElement node, string field)
    {
        JsonElement value;
        if (!node.TryGetProperty(field, out value) || value.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        int parsed;
        if (!value.TryGetInt32(out parsed))
        {
            return 0;
        }

        return parsed;
    }

    private static string BuildRemoveBody(string workflowId)
    {
        string id = workflowId.Replace("'", "");
        return "var t=localStorage.getItem('Rh-Accesstoken');"
            + "var h={'Content-Type':'application/json','client':'WEB','X-RH-Lang':'zh'};"
            + "if(t){h['Authorization']='Bearer '+t;}"
            + "var r=await fetch('" + RemoveApi + "',{method:'POST',headers:h,body:JSON.stringify({workflowId:'" + id + "'})});"
            + "window.__rhResult=await r.text();";
    }

    private static string FindCapture(string captureJson, string apiPath)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(captureJson);
            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("url", out JsonElement url))
                {
                    continue;
                }

                if ((url.GetString() ?? "").Contains(apiPath))
                {
                    return item.TryGetProperty("body", out JsonElement body) ? (body.GetString() ?? "") : "";
                }
            }
        }
        catch (JsonException)
        {
            // 捕获内容异常——按未取到处理
        }

        return "";
    }

    private static string ReadMessage(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return "";
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("msg", out JsonElement msg))
            {
                return msg.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
            // 非 JSON——原样返回片段
        }

        return Trim(payload);
    }

    private static string Trim(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        return text.Length <= 200 ? text : text.Substring(0, 200);
    }
}

/// <summary>工作流写操作结果。</summary>
public sealed class WorkflowOpResult
{
    /// <summary>是否成功（站点 code = 0）。</summary>
    public bool Ok { get; set; }

    /// <summary>站点返回的 msg。</summary>
    public string Message { get; set; } = "";

    /// <summary>原始响应片段。</summary>
    public string Response { get; set; } = "";

    /// <summary>失败原因。</summary>
    public string Error { get; set; } = "";
}
