using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiNative.Application.Integrations;
using AiNative.Infrastructure.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AiNative.Infrastructure.Integrations;

public sealed class IntegrationOptions
{
    public const string SectionName = "Integrations";

    /// <summary>
    /// 连接外部 MCP 服务（含初始化握手）的超时。
    /// </summary>
    public TimeSpan McpConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 单次读取工具列表或调用工具的超时。
    /// </summary>
    public TimeSpan McpCallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 空闲多久后关闭复用的连接。
    /// </summary>
    public TimeSpan McpIdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 工具返回文本的最大长度，超出部分截断，免得一次调用塞满模型上下文。
    /// </summary>
    public int MaxToolResultChars { get; set; } = 50_000;
}

/// <summary>
/// 外部 MCP 服务客户端。同一个服务（地址和请求头都相同）复用一个会话，省掉每次调用的初始化握手，空闲一段时间后关闭；
/// 服务重启或会话过期时丢弃旧会话重连。所有连接经过 <see cref="OutboundHttp"/> 的内网防护，不走系统代理。
/// </summary>
internal sealed class McpGateway : IMcpGateway, IAsyncDisposable
{
    private const int MaxListPages = 20;

    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly HttpClient _http;
    private readonly IntegrationOptions _options;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<McpGateway> _logger;
    private readonly TimeProvider _clock;
    private readonly ITimer _sweeper;

    public McpGateway(IOptions<IntegrationOptions> options, IOptions<OutboundNetworkOptions> network, ILoggerFactory loggers, TimeProvider clock)
    {
        _options = options.Value;
        _loggers = loggers;
        _logger = loggers.CreateLogger<McpGateway>();
        _clock = clock;
        _http = new HttpClient(OutboundHttp.CreateHandler(network.Value.AllowPrivateNetworkRequests)) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AiNative-Platform/1.0");
        var interval = TimeSpan.FromMinutes(1);
        _sweeper = clock.CreateTimer(_ => _ = SweepAsync(), null, interval, interval);
    }

    public async Task<IReadOnlyList<McpRemoteTool>> ListToolsAsync(McpEndpoint endpoint, CancellationToken cancellationToken)
    {
        // 读取列表没有副作用，复用的会话出任何连接问题都可以重连再试一次。
        var tools = await RunAsync(endpoint, retryOnTransportError: true, async (client, token) =>
        {
            var all = new List<Tool>();
            string? cursor = null;
            for (var page = 0; page < MaxListPages; page++)
            {
                var result = await client.ListToolsAsync(new ListToolsRequestParams { Cursor = cursor }, token).ConfigureAwait(false);
                all.AddRange(result.Tools);
                cursor = result.NextCursor;
                if (string.IsNullOrEmpty(cursor))
                {
                    break;
                }
            }

            return all;
        }, cancellationToken).ConfigureAwait(false);

        return [.. tools.Select(t => new McpRemoteTool(
            t.Name,
            t.Title ?? t.Annotations?.Title,
            t.Description,
            t.InputSchema.Clone(),
            t.Annotations?.ReadOnlyHint == true))];
    }

    public async Task<McpCallResult> CallToolAsync(McpEndpoint endpoint, string toolName, JsonElement arguments, CancellationToken cancellationToken)
    {
        var request = new CallToolRequestParams
        {
            Name = toolName,
            Arguments = arguments.ValueKind == JsonValueKind.Object
                ? arguments.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal)
                : new Dictionary<string, JsonElement>(),
        };

        // 调用工具可能有副作用：只在服务明确表示会话不存在（请求没被处理）时重连重试。
        var result = await RunAsync(endpoint, retryOnTransportError: false, (client, token) => client.CallToolAsync(request, token).AsTask(), cancellationToken)
            .ConfigureAwait(false);
        return ToResult(result);
    }

    public async ValueTask DisposeAsync()
    {
        await _sweeper.DisposeAsync().ConfigureAwait(false);
        foreach (var key in _sessions.Keys)
        {
            if (_sessions.TryRemove(key, out var session))
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }

        _http.Dispose();
    }

    private async Task<T> RunAsync<T>(
        McpEndpoint endpoint,
        bool retryOnTransportError,
        Func<McpClient, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.McpConnectTimeout + _options.McpCallTimeout);
        var key = SessionKey(endpoint);
        for (var attempt = 0; ; attempt++)
        {
            var session = _sessions.GetOrAdd(key, _ => new Session(() => ConnectAsync(endpoint), _clock.GetUtcNow()));
            var reused = session.UseCount > 0;
            try
            {
                var client = await session.Client.WaitAsync(timeout.Token).ConfigureAwait(false);
                if (client.Completion.IsCompleted)
                {
                    // 会话已经在后台结束（服务端断开）：还没发请求，直接换新会话。
                    await DiscardAsync(key, session).ConfigureAwait(false);
                    continue;
                }

                session.Touch(_clock);
                using var call = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                call.CancelAfter(_options.McpCallTimeout);
                try
                {
                    return await work(client, call.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (call.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new McpGatewayException($"外部服务超过 {_options.McpCallTimeout.TotalSeconds:0} 秒没有响应。");
                }
            }
            catch (McpProtocolException ex)
            {
                // 服务按协议返回的错误（工具不存在、参数不对等）：会话本身没问题。
                throw new McpGatewayException($"外部服务返回错误：{ex.Message}");
            }
            catch (McpGatewayException)
            {
                await DiscardAsync(key, session).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                await DiscardAsync(key, session).ConfigureAwait(false);
                throw new McpGatewayException($"连接外部服务超过 {_options.McpConnectTimeout.TotalSeconds:0} 秒没有完成。");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await DiscardAsync(key, session).ConfigureAwait(false);
                var sessionGone = StatusOf(ex) == HttpStatusCode.NotFound;
                if (attempt == 0 && reused && (sessionGone || retryOnTransportError))
                {
                    _logger.LogInformation(ex, "MCP session for server {ServerId} dropped, reconnecting", endpoint.ServerId);
                    continue;
                }

                _logger.LogInformation(ex, "MCP request to server {ServerId} failed", endpoint.ServerId);
                throw new McpGatewayException(Describe(ex));
            }
        }
    }

    private async Task<McpClient> ConnectAsync(McpEndpoint endpoint)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = endpoint.Url,
                TransportMode = HttpTransportMode.AutoDetect,
                AdditionalHeaders = endpoint.Headers.ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase),
                ConnectionTimeout = _options.McpConnectTimeout,
                Name = endpoint.Url.Host,
            },
            _http,
            _loggers,
            false);
        using var timeout = new CancellationTokenSource(_options.McpConnectTimeout);
        return await McpClient.CreateAsync(
            transport,
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "ai-native-platform", Title = "AI Native 平台", Version = "1.0.0" },
                InitializationTimeout = _options.McpConnectTimeout,
            },
            _loggers,
            timeout.Token).ConfigureAwait(false);
    }

    private async Task DiscardAsync(string key, Session session)
    {
        if (_sessions.TryRemove(new KeyValuePair<string, Session>(key, session)))
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task SweepAsync()
    {
        var cutoff = _clock.GetUtcNow() - _options.McpIdleTimeout;
        foreach (var (key, session) in _sessions)
        {
            if (session.LastUsed < cutoff)
            {
                try
                {
                    await DiscardAsync(key, session).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Closing idle MCP session failed");
                }
            }
        }
    }

    private McpCallResult ToResult(CallToolResult result)
    {
        var parts = result.Content
            .Select(c => c switch
            {
                TextContentBlock text => text.Text,
                ImageContentBlock image => $"[图片 {image.MimeType}]",
                AudioContentBlock audio => $"[音频 {audio.MimeType}]",
                EmbeddedResourceBlock { Resource: TextResourceContents resource } => resource.Text,
                EmbeddedResourceBlock embedded => $"[资源 {embedded.Resource.Uri}]",
                ResourceLinkBlock link => $"[资源链接 {link.Name}：{link.Uri}]",
                _ => null,
            })
            .Where(p => !string.IsNullOrEmpty(p));
        var text = string.Join("\n\n", parts);
        var max = _options.MaxToolResultChars;
        if (text.Length > max)
        {
            text = text[..max] + "\n…（内容过长，后面的部分已截断）";
        }

        JsonElement? structured = result.StructuredContent is { } value && value.GetRawText().Length <= max ? value.Clone() : null;
        return new McpCallResult(result.IsError == true, text, structured);
    }

    /// <summary>
    /// 地址和请求头都相同才复用会话；改了令牌就是新会话。请求头只以哈希出现在键里。
    /// </summary>
    private static string SessionKey(McpEndpoint endpoint)
    {
        var headers = string.Join("\n", endpoint.Headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase).Select(h => $"{h.Key.ToLowerInvariant()}:{h.Value}"));
        return $"{endpoint.ServerId:N}|{endpoint.Url}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(headers)))}";
    }

    private static HttpStatusCode? StatusOf(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: { } status })
            {
                return status;
            }
        }

        return null;
    }

    private static string Describe(Exception exception)
    {
        if (OutboundHttp.FindBlocked(exception) is { } blocked)
        {
            return blocked.Message;
        }

        return StatusOf(exception) switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"外部服务拒绝访问（HTTP {(int)StatusOf(exception)!}），请检查请求头里的令牌。",
            { } status => $"外部服务返回 HTTP {(int)status}。",
            null => exception is McpException ? $"外部服务出错：{exception.Message}" : $"连接外部服务失败：{exception.Message}",
        };
    }

    /// <summary>
    /// 复用的会话。连接在第一次使用时才建立：并发 GetOrAdd 多创建出来的对象不会白白发起连接。
    /// </summary>
    private sealed class Session(Func<Task<McpClient>> connect, DateTimeOffset createdAt) : IAsyncDisposable
    {
        private readonly Lazy<Task<McpClient>> _client = new(connect, LazyThreadSafetyMode.ExecutionAndPublication);
        private long _lastUsedTicks = createdAt.UtcTicks;
        private int _useCount;

        public Task<McpClient> Client => _client.Value;

        public int UseCount => Volatile.Read(ref _useCount);

        public DateTimeOffset LastUsed => new(Interlocked.Read(ref _lastUsedTicks), TimeSpan.Zero);

        public void Touch(TimeProvider clock)
        {
            Interlocked.Increment(ref _useCount);
            Interlocked.Exchange(ref _lastUsedTicks, clock.GetUtcNow().UtcTicks);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_client.IsValueCreated)
            {
                return;
            }

            try
            {
                var client = await _client.Value.ConfigureAwait(false);
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 没连上的会话无需关闭；关闭时出错也不影响后续。
            }
        }
    }
}
