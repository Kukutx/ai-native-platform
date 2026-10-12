using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AiNative.Application.Workflows;
using Microsoft.Extensions.Options;

namespace AiNative.Infrastructure.Workflows;

/// <summary>
/// 「HTTP 请求」步骤的出站客户端。整个进程共用一个连接池；每次建立连接时检查目标地址，
/// 默认拒绝本机、内网、链路本地等地址，重定向和 DNS 指向内网的域名同样被挡住（检查的是实际要连的 IP）。
/// 不走系统代理：经过代理时只能检查代理本身的地址，起不到防护作用。
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

    public WorkflowHttpClient(IOptions<WorkflowOptions> options)
    {
        var allowPrivate = options.Value.AllowPrivateNetworkRequests;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ConnectCallback = (context, cancellationToken) => ConnectAsync(context.DnsEndPoint, allowPrivate, cancellationToken),
        };
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
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
            throw ex.InnerException as WorkflowHttpException ?? new WorkflowHttpException($"请求失败：{ex.Message}");
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

    private static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, bool allowPrivate, CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);
        var allowed = allowPrivate ? addresses : [.. addresses.Where(IsPublic)];
        if (allowed.Length == 0)
        {
            throw new WorkflowHttpException(addresses.Length == 0
                ? $"无法解析域名 {endpoint.Host}。"
                : $"不允许访问内网或本机地址（{endpoint.Host}）。需要时由管理员设置 Workflows__AllowPrivateNetworkRequests=true。");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, endpoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 公网单播地址。排除本机、私有网段、运营商级 NAT、链路本地、文档与测试网段、组播和保留地址。
    /// </summary>
    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] is 0 or 10 or 127 or >= 224
                     || (b[0] == 100 && b[1] is >= 64 and <= 127)
                     || (b[0] == 169 && b[1] == 254)
                     || (b[0] == 172 && b[1] is >= 16 and <= 31)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2)
                     || (b[0] == 198 && b[1] is 18 or 19)
                     || (b[0] == 198 && b[1] == 51 && b[2] == 100)
                     || (b[0] == 203 && b[1] == 0 && b[2] == 113));
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !(address.Equals(IPAddress.IPv6Any)
                     || address.IsIPv6LinkLocal
                     || address.IsIPv6SiteLocal
                     || address.IsIPv6UniqueLocal
                     || address.IsIPv6Multicast
                     || address.IsIPv6Teredo);
        }

        return false;
    }
}
