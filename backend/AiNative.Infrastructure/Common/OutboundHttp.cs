using System.Net;
using System.Net.Sockets;

namespace AiNative.Infrastructure.Common;

public sealed class OutboundNetworkOptions
{
    public const string SectionName = "OutboundNetwork";

    /// <summary>
    /// 是否允许平台主动发出的请求（工作流「HTTP 请求」步骤、外部 MCP 服务）访问内网和本机地址。
    /// 默认关闭，防止借这些功能探测内网；需要调用同一内网里的服务时再打开。
    /// </summary>
    public bool AllowPrivateNetworkRequests { get; set; }
}

/// <summary>
/// 目标地址是内网或本机，被出站防护拒绝。HttpClient 会把它包在 HttpRequestException 里抛出。
/// </summary>
public sealed class PrivateNetworkBlockedException(string message) : IOException(message);

/// <summary>
/// 平台主动访问外部地址时共用的连接处理器。每次建立连接时检查目标地址，默认拒绝本机、内网、链路本地等地址，
/// 重定向和 DNS 指向内网的域名同样被挡住（检查的是实际要连的 IP）。
/// 不走系统代理：经过代理时只能检查代理本身的地址，起不到防护作用。
/// 不保存 Cookie：处理器被所有空间共用，自动保存的 Cookie 会按地址发给别的空间的请求。
/// </summary>
public static class OutboundHttp
{
    public static SocketsHttpHandler CreateHandler(bool allowPrivateNetwork, bool allowRedirects = true) => new()
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = allowRedirects,
        MaxAutomaticRedirections = 5,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        ConnectCallback = (context, cancellationToken) => ConnectAsync(context.DnsEndPoint, allowPrivateNetwork, cancellationToken),
    };

    /// <summary>
    /// 从 HttpClient 抛出的异常里找出被出站防护拒绝的原因。
    /// </summary>
    public static PrivateNetworkBlockedException? FindBlocked(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PrivateNetworkBlockedException blocked)
            {
                return blocked;
            }
        }

        return null;
    }

    private static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, bool allowPrivate, CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);
        var allowed = allowPrivate ? addresses : [.. addresses.Where(IsPublic)];
        if (allowed.Length == 0)
        {
            throw new PrivateNetworkBlockedException(addresses.Length == 0
                ? $"无法解析域名 {endpoint.Host}。"
                : $"不允许访问内网或本机地址（{endpoint.Host}）。需要时由管理员设置 OutboundNetwork__AllowPrivateNetworkRequests=true。");
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
    public static bool IsPublic(IPAddress address)
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
