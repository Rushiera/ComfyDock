using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Collect;

/// <summary>
/// 工作流写操作——上传与删除。全程在浏览器页面内完成（同源 + 浏览器自带的登录凭据），
/// **凭据不出浏览器**：不读 token 到本进程、不落盘 token。
/// </summary>
public sealed class WorkflowAdmin
{
    private const string RemoveApi = "/api/workflow/remove";
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

    /// <summary>CDP 会话入口（受控实例优先）。</summary>
    private readonly CdpGateway _gateway;

    /// <summary>工作台地址。</summary>
    private readonly string _workspaceUrl;

    /// <summary>构造工作流写操作器。</summary>
    /// <param name="gateway">CDP 会话入口（受控实例优先，其次 headless 临时实例）。</param>
    /// <param name="workspaceUrl">工作台地址。</param>
    public WorkflowAdmin(CdpGateway gateway, string workspaceUrl)
    {
        _gateway = gateway;
        _workspaceUrl = workspaceUrl;
    }

    /// <summary>删除一个工作流（幂等语义由站点决定；本方法只报站点返回）。</summary>
    /// <param name="profileDir">浏览器用户数据目录。</param>
    /// <param name="workflowId">工作流 id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>操作结果。</returns>
    public async Task<WorkflowOpResult> DeleteAsync(string profileDir, string workflowId, CancellationToken ct)
    {
        var result = new WorkflowOpResult();

        // [段1] 取会话——受控实例优先；被面板之外的实例占用则明确报错
        CdpLease lease;
        try
        {
            lease = await _gateway.AcquireAsync(profileDir, ct).ConfigureAwait(false);
        }
        catch (BrowserBusyException ex)
        {
            result.Error = ex.Message;
            return result;
        }

        // [段2] 页面内执行删除
        await using (lease)
        {
            await RunInPageAsync(lease.Session, BuildRemoveScript(workflowId), result, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>上传一个工作流文件（走页面里的 input[type=file]，由浏览器读取本地文件）。</summary>
    /// <param name="profileDir">浏览器用户数据目录。</param>
    /// <param name="jsonPath">工作流 JSON 完整路径。</param>
    /// <param name="waitSeconds">等待上传接口响应的秒数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>操作结果。</returns>
    public async Task<WorkflowOpResult> UploadAsync(string profileDir, string jsonPath, int waitSeconds, CancellationToken ct)
    {
        var result = new WorkflowOpResult();

        if (!File.Exists(jsonPath))
        {
            result.Error = "工作流文件不存在：" + jsonPath;
            return result;
        }

        // [段1] 取会话——受控实例优先；被面板之外的实例占用则明确报错
        CdpLease lease;
        try
        {
            lease = await _gateway.AcquireAsync(profileDir, ct).ConfigureAwait(false);
        }
        catch (BrowserBusyException ex)
        {
            result.Error = ex.Message;
            return result;
        }

        await using (lease)
        {
            CdpSession session = lease.Session;

            // [段2] 装 XHR 钩子并打开工作台
            await session.EnablePageAsync(ct).ConfigureAwait(false);
            await session.EnableDomAsync(ct).ConfigureAwait(false);
            await session.AddInitScriptAsync(HookScript, ct).ConfigureAwait(false);
            await session.NavigateAsync(_workspaceUrl, ct).ConfigureAwait(false);

            // [段3] 等页面把文件输入框渲染出来
            int nodeId = 0;
            for (int i = 0; i < 30 && nodeId == 0; i++)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                int root = await session.GetRootNodeIdAsync(ct).ConfigureAwait(false);
                if (root == 0)
                {
                    continue;
                }

                nodeId = await session.QuerySelectorAsync(root, "input[type=file]", ct).ConfigureAwait(false);
            }

            if (nodeId == 0)
            {
                result.Error = "页面上没找到文件输入框（导入入口可能已改版）";
                return result;
            }

            await session.SetFileInputFilesAsync(nodeId, jsonPath, ct).ConfigureAwait(false);

            // [段4] 等上传接口的响应
            for (int i = 0; i < waitSeconds; i++)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);

                string raw = await session.EvaluateAsync("JSON.stringify(window.__rhCapture||[])", false, ct).ConfigureAwait(false);
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
        }

        return result;
    }
    /// <summary>在已取得的页面通道里导航到工作台并执行脚本（结果写回 result）。</summary>
    /// <param name="session">CDP 会话。</param>
    /// <param name="script">要执行的 JS。</param>
    /// <param name="result">结果载体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    private async Task RunInPageAsync(CdpSession session, string script, WorkflowOpResult result, CancellationToken ct)
    {
        // [段1] 打开工作台页并等单页应用把数据拉起来
        await session.EnablePageAsync(ct).ConfigureAwait(false);
        await session.NavigateAsync(_workspaceUrl, ct).ConfigureAwait(false);
        await Task.Delay(6000, ct).ConfigureAwait(false);

        // [段2] 页面内执行并解读站点返回
        string output = await session.EvaluateAsync(script, true, ct).ConfigureAwait(false);
        result.Response = output;
        result.Message = ReadMessage(output);
        result.Ok = output.Contains("\"code\":0");
        if (!result.Ok && result.Message.Length == 0)
        {
            result.Error = "站点未返回可识别结果：" + Trim(output);
        }
    }

    private static string BuildRemoveScript(string workflowId)
    {
        string id = workflowId.Replace("'", "");
        return "(async function(){"
            + "var t=localStorage.getItem('Rh-Accesstoken');"
            + "var h={'Content-Type':'application/json','client':'WEB','X-RH-Lang':'zh'};"
            + "if(t){h['Authorization']='Bearer '+t;}"
            + "var r=await fetch('" + RemoveApi + "',{method:'POST',headers:h,body:JSON.stringify({workflowId:'" + id + "'})});"
            + "return await r.text();"
            + "})()";
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
