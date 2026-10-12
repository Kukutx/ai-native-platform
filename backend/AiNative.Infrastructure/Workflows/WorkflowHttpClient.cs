using System.Text;
using System.Text.Json;
using AiNative.Application.Workflows;
using AiNative.Infrastructure.Common;
using Microsoft.Extensions.Options;

namespace AiNative.Infrastructure.Workflows;

/// <summary>
/// 「HTTP 请求」步骤的出站客户端。整个进程共用一个连接池，连接经过 <see cref="OutboundHttp"/> 的内网防护。
/// </summary>
internal sealed class WorkflowHttpClient : IWorkflowHttpClient, IDisposable
{
    public const int MaxResponseBytes = 1024 * 1024;

    private readonly HttpClient _client;

    static WorkflowHttpClient()
    {
        // 国内不少接口返回 GBK / GB18030，运行时默认只带 UTF 系列编码。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public WorkflowHttpClient(IOptions<OutboundNetworkOptions> options)
    {
        _client = new HttpClient(OutboundHttp.CreateHandler(options.Value.AllowPrivateNetworkRequests)) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("AiNative-Workflow/1.0");
    }

    public async Task<WorkflowHttpResponse> SendAsync(WorkflowHttpRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        using var message = BuildMessage(request);
        try
        {
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType;
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                throw TooLarge();
            }

            var bytes = await ReadLimitedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            return new WorkflowHttpResponse((int)response.StatusCode, contentType?.MediaType, Decode(bytes, contentType?.CharSet));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WorkflowHttpException($"请求超过 {request.Timeout.TotalSeconds:0} 秒没有完成。");
        }
        catch (HttpRequestException ex)
        {
            // 连接回调里拒绝的地址会被包一层。
            throw new WorkflowHttpException(OutboundHttp.FindBlocked(ex)?.Message ?? $"请求失败：{ex.Message}");
        }
    }

    public void Dispose() => _client.Dispose();

    private static HttpRequestMessage BuildMessage(WorkflowHttpRequest request)
    {
        var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        var contentHeaders = new List<KeyValuePair<string, string>>();
        foreach (var (name, value) in request.Headers)
        {
            if (!message.Headers.TryAddWithoutValidation(name, value))
            {
                contentHeaders.Add(new(name, value));
            }
        }

        if (request.Body is { } body)
        {
            message.Content = body.ValueKind == JsonValueKind.String
                ? new StringContent(body.GetString() ?? string.Empty, Encoding.UTF8, "text/plain")
                : new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");
            foreach (var (name, value) in contentHeaders)
            {
                message.Content.Headers.Remove(name);
                message.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return message;
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                throw TooLarge();
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string Decode(byte[] bytes, string? charset)
    {
        var encoding = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
            }
        }

        return encoding.GetString(bytes);
    }

    private static WorkflowHttpException TooLarge() => new($"响应超过 {MaxResponseBytes / 1024 / 1024} MB，已放弃。");
}
