using System.Net;

namespace AiNative.Infrastructure.Common;

/// <summary>
/// 跟随重定向（最多 5 次）。同一来源（协议、主机、端口都相同）的跳转保留全部请求头；
/// 跳到其他来源时只保留不含凭据的通用请求头。HttpClient 自带的重定向只去掉 Authorization，
/// X-Api-Key、Private-Token 这类自定义密钥头会被原样带到新主机。
/// 不允许从 https 降级到 http，与 HttpClient 的默认行为一致。
/// </summary>
public sealed class SafeRedirectHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    private const int MaxRedirects = 5;

    private static readonly HashSet<string> CrossOriginHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "Accept-Language", "User-Agent", "Cache-Control", "Pragma",
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        for (var hops = 0; hops < MaxRedirects && NextRequest(request, response) is { } next; hops++)
        {
            response.Dispose();
            request = next;
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    private static HttpRequestMessage? NextRequest(HttpRequestMessage request, HttpResponseMessage response)
    {
        if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } location || request.RequestUri is not { } current)
        {
            return null;
        }

        var target = location.IsAbsoluteUri ? location : new Uri(current, location);
        if ((target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps)
            || (current.Scheme == Uri.UriSchemeHttps && target.Scheme == Uri.UriSchemeHttp))
        {
            return null;
        }

        // 301/302/303 改用 GET 并丢弃请求体（浏览器与 HttpClient 的惯例）；307/308 原样重发。
        var keepMethod = response.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
                         || (request.Method == HttpMethod.Head);
        var next = new HttpRequestMessage(keepMethod ? request.Method : HttpMethod.Get, target)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
            Content = keepMethod ? request.Content : null,
        };
        var sameOrigin = string.Equals(current.GetLeftPart(UriPartial.Authority), target.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            if (sameOrigin || CrossOriginHeaders.Contains(header.Key))
            {
                next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        foreach (var option in request.Options)
        {
            ((IDictionary<string, object?>)next.Options)[option.Key] = option.Value;
        }

        return next;
    }

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
