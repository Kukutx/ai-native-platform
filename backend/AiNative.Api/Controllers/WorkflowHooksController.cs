using System.Text.Json;
using AiNative.Application.Common;
using AiNative.Application.Workflows;
using AiNative.Contracts.Workflows;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiNative.Api.Controllers;

/// <summary>
/// 外部系统（表单、代码仓库、支付回调、其他自动化平台）调用 Webhook 地址触发工作流。
/// 地址里的令牌就是凭证，不需要登录；请求体、查询参数和请求头放进 trigger，供步骤引用。
/// </summary>
[AllowAnonymous]
[Route("api/hooks/workflows")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class WorkflowHooksController : ApiControllerBase
{
    private const int MaxBodyBytes = 256 * 1024;

    // 凭证类请求头不进入运行记录。
    private static readonly HashSet<string> ExcludedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie",
    };

    [HttpPost("{token}")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Receive(string token)
    {
        var body = await ReadBodyAsync().ConfigureAwait(false);
        var query = Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.Ordinal);
        var headers = Request.Headers
            .Where(h => !ExcludedHeaders.Contains(h.Key))
            .ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString(), StringComparer.Ordinal);
        var result = await SendAsync(new ReceiveWorkflowWebhookCommand(token, body, query, headers)).ConfigureAwait(false);
        return Accepted(result);
    }

    /// <summary>
    /// JSON 按 JSON 解析，表单转成字段对象，其他内容按文本保留。
    /// </summary>
    private async Task<JsonElement?> ReadBodyAsync()
    {
        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            return PlatformJson.ToElement(form.ToDictionary(f => f.Key, f => f.Value.ToString(), StringComparer.Ordinal));
        }

        using var reader = new StreamReader(Request.Body);
        var text = await reader.ReadToEndAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        if (text.Length == 0)
        {
            return null;
        }

        var mediaType = Request.ContentType ?? string.Empty;
        if (!mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return PlatformJson.ToElement(text);
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ValidationFailedException(new Dictionary<string, string[]> { ["body"] = ["请求体不是合法的 JSON。"] });
        }
    }
}
