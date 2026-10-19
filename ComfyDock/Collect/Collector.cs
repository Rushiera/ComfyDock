using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyDock.Models;

namespace ComfyDock.Collect;

/// <summary>
/// 采集器——**一次套壳浏览器会话**同时取回「登录态 + RH 币余额」与「工作流列表」。
/// 走软件内置套壳（WebView2）在账号自己的数据目录里执行：人工登录与自动采集共用同一份 profile，凭据不出浏览器。
/// </summary>
public sealed class Collector
{
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
  XMLHttpRequest.prototype.send = function(body){
    var self = this;
    // 任务账单页默认一页 20 条——放大页长，24 小时窗口内的花费才不会被截断
    try {
      if (self.__rhUrl && self.__rhUrl.indexOf('/api/billing/usage/wideDetails') >= 0 && body) {
        var payload = JSON.parse(body);
        payload.size = 200;
        body = JSON.stringify(payload);
      }
    } catch (e) {}
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
    return send.call(this, body);
  };
})();";

    /// <summary>在页面里解引用 Nuxt payload，取回登录用户的关键字段（小 JSON）。</summary>
    private const string ReadUserScript = @"(function(){
  var el = document.getElementById('__NUXT_DATA__');
  if (!el) { return ''; }
  var text = el.textContent || '';
  var key = '$srh-login-user-info';
  var at = text.indexOf(key);
  if (at < 0) { return 'null'; }
  var colon = text.indexOf(':', at + key.length);
  var stop = text.indexOf(',', colon);
  if (colon < 0 || stop < 0) { return ''; }
  var idx = parseInt(text.substring(colon + 1, stop), 10);
  if (isNaN(idx)) { return ''; }
  var nodes;
  try { nodes = JSON.parse(text); } catch (e) { return ''; }
  function res(v, d){
    if (d > 8) { return v; }
    if (typeof v === 'number') { return res(nodes[v], d + 1); }
    if (Object.prototype.toString.call(v) === '[object Array]' && v.length === 2 &&
        (v[0] === 'Reactive' || v[0] === 'ShallowReactive' || v[0] === 'Ref')) {
      return res(nodes[v[1]], d + 1);
    }
    return v;
  }
  var user = res(nodes[idx], 0);
  if (!user || typeof user !== 'object') { return 'null'; }
  function pick(k){ var v = user[k]; return v === undefined ? null : res(v, 0); }
  return JSON.stringify({
    id: pick('id'),
    mobile: pick('mobile'),
    nickName: pick('nickName'),
    totalCoin: pick('totalCoin'),
    virtualCoin: pick('virtualCoin'),
    apiKey: pick('apiKey')
  });
})()";

    private const string WorkflowApiPath = "/api/workflow/page/simpleList";

    /// <summary>算力获取明细接口（买币历史页「算力获取明细」）——每日赠送一笔、24 小时到期。</summary>
    private const string GainApiPath = "/api/product/coinGainList";

    /// <summary>任务费用明细接口（任务账单页「任务记录与费用」）。</summary>
    private const string SpendApiPath = "/api/billing/usage/wideDetails";

    /// <summary>算力获取明细页地址（「上次获取算力时间 / 刷新时间」的来源）。</summary>
    private const string GainPageUrl = "https://www.runninghub.cn/buy-history?tab=2";

    /// <summary>任务账单页地址（「自上次获取算力以来的花费」的来源）。</summary>
    private const string BillPageUrl = "https://www.runninghub.cn/bill-task?tab=detail";

    /// <summary>等待页面数据时的轮询间隔（毫秒）——先探再等，不用等满一秒才看第一眼。</summary>
    private const int PollIntervalMs = 250;

    /// <summary>套壳浏览器客户端（WebView2，账号自己的数据目录）。</summary>
    private readonly ShellClient _shell;

    /// <summary>首页地址（SSR payload 带登录态与余额）。</summary>
    private readonly string _homeUrl;

    /// <summary>工作台地址（XHR 拉工作流列表）。</summary>
    private readonly string _workspaceUrl;

    /// <summary>等待页面数据的秒数上限。</summary>
    private readonly int _waitSeconds;

    /// <summary>构造采集器。</summary>
    /// <param name="shell">套壳浏览器客户端。</param>
    /// <param name="homeUrl">首页地址（SSR payload 带登录态与余额）。</param>
    /// <param name="workspaceUrl">工作台地址（XHR 拉工作流列表）。</param>
    /// <param name="waitSeconds">等待页面数据的秒数上限。</param>
    public Collector(ShellClient shell, string homeUrl, string workspaceUrl, int waitSeconds = 45)
    {
        _shell = shell;
        _homeUrl = homeUrl;
        _workspaceUrl = workspaceUrl;
        _waitSeconds = waitSeconds;
    }

    /// <summary>采集单个账号（在套壳里打开该账号的数据目录逐页取数）。</summary>
    /// <param name="accountId">账号 id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>采集结果（余额 + 工作流列表）。</returns>
    public async Task<CollectResult> CollectAsync(string accountId, CancellationToken ct)
    {
        var result = new CollectResult();

        // [段1] 账号 id 可用性
        if (string.IsNullOrWhiteSpace(accountId))
        {
            result.Status = "error";
            result.Error = "账号 id 为空";
            return result;
        }

        // [段2] 装页面钩子——XHR 捕获脚本在每个新文档创建时执行（同一账号视图只装一次）
        try
        {
            await _shell.AddInitScriptAsync(accountId, HookScript).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.Message;
            return result;
        }

        try
        {
            // [段3] 第一步：首页 SSR payload 带登录态与余额
            await _shell.OpenAsync(accountId, _homeUrl, false, 60000).ConfigureAwait(false);
            string userJson = await WaitForUserAsync(accountId, ct).ConfigureAwait(false);
            ApplyUser(result, userJson);

            // [段4] 未登录 / 取不到登录数据——直接收工，省掉第二段导航（工作台页必然也拿不到东西）
            if (!string.Equals(result.Status, "ok", StringComparison.Ordinal))
            {
                return result;
            }

            // [段5] 第二步：工作台页加载后由 XHR 拉工作流列表
            await _shell.OpenAsync(accountId, _workspaceUrl, false, 60000).ConfigureAwait(false);
            string workflowBody = await WaitForCaptureAsync(accountId, WorkflowApiPath, ct).ConfigureAwait(false);
            ApplyWorkflows(result, workflowBody);

            // [段6] 第三步：算力获取明细——每日赠送一笔、24 小时到期，取最新一条的获取时间与额度
            await _shell.OpenAsync(accountId, GainPageUrl, false, 60000).ConfigureAwait(false);
            string gainBody = await WaitForCaptureAsync(accountId, GainApiPath, ct).ConfigureAwait(false);
            ApplyGain(result, gainBody);

            // [段7] 第四步：任务费用明细——只累加「上次获取算力之后」的 RH 币花费（没有获取记录就不必跑）
            if (result.CoinGainTime.Length > 0)
            {
                await _shell.OpenAsync(accountId, BillPageUrl, false, 60000).ConfigureAwait(false);
                string spendBody = await WaitForCaptureAsync(accountId, SpendApiPath, ct).ConfigureAwait(false);
                ApplySpend(result, spendBody);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.Message;
        }

        return result;
    }
    /// <summary>容错求值——页面在忙时求值会超时，按「暂时取不到」处理，让轮询继续。</summary>
    /// <param name="accountId">账号 id。</param>
    /// <param name="script">脚本正文。</param>
    /// <returns>结果文本；超时或失败返回空串。</returns>
    private async Task<string> SafeEvalAsync(string accountId, string script)
    {
        try
        {
            return await _shell.EvalTextAsync(accountId, script, 20000).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return "";
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }

    private async Task<string> WaitForUserAsync(string accountId, CancellationToken ct)
    {
        int rounds = _waitSeconds * 1000 / PollIntervalMs;
        for (int i = 0; i < rounds; i++)
        {
            await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);
            string json = await SafeEvalAsync(accountId, ReadUserScript).ConfigureAwait(false);
            if (json.Length > 0)
            {
                return json;
            }
        }

        return "";
    }
    /// <summary>等页面自己把某个接口的响应发出来（XHR 钩子捕获），取回响应体。</summary>
    /// <param name="accountId">账号 id。</param>
    /// <param name="apiPath">目标接口路径片段。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>响应体；等待超时返回空串。</returns>
    private async Task<string> WaitForCaptureAsync(string accountId, string apiPath, CancellationToken ct)
    {
        int rounds = _waitSeconds * 1000 / PollIntervalMs;
        for (int i = 0; i < rounds; i++)
        {
            await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);
            string raw = await SafeEvalAsync(accountId, "JSON.stringify(window.__rhCapture||[])").ConfigureAwait(false);
            string body = ExtractCaptureBody(raw, apiPath);
            if (body.Length > 0)
            {
                return body;
            }
        }

        return "";
    }

    private static void ApplyUser(CollectResult result, string userJson)
    {
        if (userJson.Length == 0)
        {
            result.Status = "error";
            result.Error = "未取到页面登录数据（页面可能未加载完）";
            return;
        }

        if (userJson == "null")
        {
            result.Status = "not-logged-in";
            result.Error = "该浏览器用户未登录";
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(userJson);
            JsonElement root = doc.RootElement;
            result.Status = "ok";
            result.UserId = ReadText(root, "id");
            result.NickName = ReadText(root, "nickName");
            result.Mobile = ReadText(root, "mobile");
            result.TotalCoin = ReadNumber(root, "totalCoin");
            result.ApiKey = ReadText(root, "apiKey");
        }
        catch (JsonException ex)
        {
            result.Status = "error";
            result.Error = "登录数据解析失败：" + ex.Message;
        }
    }

    private static void ApplyWorkflows(CollectResult result, string workflowBody)
    {
        if (workflowBody.Length == 0)
        {
            result.WorkflowError = "未取到工作流列表（接口未返回）";
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(workflowBody);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
            {
                result.WorkflowError = "工作流接口返回异常结构";
                return;
            }

            if (data.TryGetProperty("total", out JsonElement total) && int.TryParse(AsText(total), out int totalCount))
            {
                result.WorkflowTotal = totalCount;
            }

            if (!data.TryGetProperty("records", out JsonElement records) || records.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (JsonElement record in records.EnumerateArray())
            {
                string workflowId = ReadText(record, "workflowId");
                if (workflowId.Length == 0)
                {
                    workflowId = ReadText(record, "id");
                }

                result.Workflows.Add(new WorkflowItem
                {
                    WorkflowId = workflowId,
                    Name = ReadText(record, "workflowName"),
                    LastUpdateTime = ReadText(record, "lastUpdateTime"),
                    NodeCount = ReadText(record, "nodeCount"),
                    TopFlag = record.TryGetProperty("topFlag", out JsonElement top) && top.ValueKind == JsonValueKind.True,
                    EditUrl = workflowId.Length == 0 ? "" : "https://www.runninghub.cn/workflow/" + workflowId,
                });
            }
        }
        catch (JsonException ex)
        {
            result.WorkflowError = "工作流数据解析失败：" + ex.Message;
        }
    }

    /// <summary>解析「算力获取明细」——取获取时间最新的一条，记下获取时间 / 额度 / 刷新（到期）时间。</summary>
    /// <param name="result">采集结果。</param>
    /// <param name="gainBody">接口响应体。</param>
    private static void ApplyGain(CollectResult result, string gainBody)
    {
        if (gainBody.Length == 0)
        {
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(gainBody);
            if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (!data.TryGetProperty("records", out JsonElement records) || records.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            string latest = "";
            long? amount = null;
            string expire = "";
            foreach (JsonElement record in records.EnumerateArray())
            {
                string gainTime = ReadText(record, "gainTime");
                if (gainTime.Length == 0 || string.CompareOrdinal(gainTime, latest) <= 0)
                {
                    continue;
                }

                latest = gainTime;
                amount = ReadAmount(record, "amount");
                expire = ReadText(record, "expiredTime");
            }

            result.CoinGainTime = latest;
            result.CoinGainAmount = amount;
            result.CoinRefreshTime = expire;
        }
        catch (JsonException)
        {
            // 捕获内容异常——按未取到处理（说明行少一段，不影响余额与工作流）
        }
    }

    /// <summary>解析「任务费用明细」——只累加发起时间不早于上次获取算力的记录，那之后的才算本笔算力的消耗。</summary>
    /// <param name="result">采集结果（需已有 CoinGainTime）。</param>
    /// <param name="spendBody">接口响应体。</param>
    private static void ApplySpend(CollectResult result, string spendBody)
    {
        if (spendBody.Length == 0 || result.CoinGainTime.Length == 0)
        {
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(spendBody);
            if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (!data.TryGetProperty("records", out JsonElement records) || records.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            double sum = 0;
            foreach (JsonElement record in records.EnumerateArray())
            {
                string createTime = ReadText(record, "createTime");
                if (createTime.Length == 0 || string.CompareOrdinal(createTime, result.CoinGainTime) < 0)
                {
                    continue;
                }

                double? used = ReadAmountValue(record, "coinAmount");
                if (used.HasValue)
                {
                    sum = sum + used.Value;
                }
            }

            result.CoinSpent = (long)Math.Round(sum);
        }
        catch (JsonException)
        {
            // 捕获内容异常——按未取到处理
        }
    }

    /// <summary>读一个可能带小数的 RH 币数值（费用明细的 coinAmount 形如 4.0）。</summary>
    /// <param name="parent">JSON 对象。</param>
    /// <param name="name">字段名。</param>
    /// <returns>数值；取不到返回 null。</returns>
    private static double? ReadAmountValue(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetDouble();
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            string raw = (value.GetString() ?? "").Replace(",", "").Replace(" ", "");
            if (double.TryParse(raw, out double parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>读一个 RH 币整数额度（获取明细的 amount）。</summary>
    /// <param name="parent">JSON 对象。</param>
    /// <param name="name">字段名。</param>
    /// <returns>整数额度；取不到返回 null。</returns>
    private static long? ReadAmount(JsonElement parent, string name)
    {
        double? value = ReadAmountValue(parent, name);
        if (!value.HasValue)
        {
            return null;
        }

        return (long)Math.Round(value.Value);
    }

    /// <summary>从钩子捕获的响应清单里取某个接口的响应体。</summary>
    /// <param name="captureJson">捕获清单 JSON。</param>
    /// <param name="apiPath">目标接口路径片段。</param>
    /// <returns>响应体；未捕获到返回空串。</returns>
    private static string ExtractCaptureBody(string captureJson, string apiPath)
    {
        if (captureJson.Length <= 4)
        {
            return "";
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(captureJson);
            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("url", out JsonElement url))
                {
                    continue;
                }

                string path = url.GetString() ?? "";
                if (path.Contains(apiPath))
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

    private static string ReadText(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out JsonElement value))
        {
            return "";
        }

        return AsText(value);
    }

    private static string AsText(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
    }

    /// <summary>从 JSON 对象里读一个整数——兼容数字、纯数字字符串，以及带千分位分隔符的格式化字符串（站点实测 "1,100"：余额过千后不再返回纯数字）。</summary>
    private static long? ReadNumber(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long direct))
        {
            return direct;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            string raw = value.GetString() ?? "";
            if (long.TryParse(raw, out long parsed))
            {
                return parsed;
            }

            // 站点把余额渲染成带千分位的格式化字符串（实测 "1,100"）——剥掉分隔符再解析
            string plain = raw.Replace(",", "").Replace(" ", "");
            if (long.TryParse(plain, out long normalized))
            {
                return normalized;
            }
        }

        return null;
    }
}

/// <summary>单槽位采集结果。</summary>
public sealed class CollectResult
{
    /// <summary>状态：ok / not-logged-in / error / running。</summary>
    public string Status { get; set; } = "error";

    /// <summary>平台用户 id。</summary>
    public string UserId { get; set; } = "";

    /// <summary>平台昵称。</summary>
    public string NickName { get; set; } = "";

    /// <summary>绑定手机号。</summary>
    public string Mobile { get; set; } = "";
    /// <summary>消费级 API 密钥（完整值——落盘 accounts.json；面板只展示前 4 位）。</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>RH 币余额（totalCoin）。</summary>
    public long? TotalCoin { get; set; }

    /// <summary>上次获取算力时间（「算力获取明细」最新一条的获取时间）；未取到为空。</summary>
    public string CoinGainTime { get; set; } = "";

    /// <summary>上次获取的算力额度（RH 币）；未取到为 null。</summary>
    public long? CoinGainAmount { get; set; }

    /// <summary>该笔算力的刷新时间（固定为获取时间后 24 小时整）；未取到为空。</summary>
    public string CoinRefreshTime { get; set; } = "";

    /// <summary>自上次获取算力以来的 RH 币花费（任务账单明细求和）；未取到为 null。</summary>
    public long? CoinSpent { get; set; }

    /// <summary>状态说明 / 失败原因。</summary>
    public string Error { get; set; } = "";

    /// <summary>工作流列表。</summary>
    public List<WorkflowItem> Workflows { get; set; } = new List<WorkflowItem>();

    /// <summary>工作流总数（服务端 total）。</summary>
    public int WorkflowTotal { get; set; }

    /// <summary>工作流采集失败原因（空 = 正常）。</summary>
    public string WorkflowError { get; set; } = "";
}
