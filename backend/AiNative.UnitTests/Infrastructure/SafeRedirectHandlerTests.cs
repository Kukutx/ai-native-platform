using System.Net;
using AiNative.Infrastructure.Common;

namespace AiNative.UnitTests.Infrastructure;

public sealed class SafeRedirectHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Cross_origin_redirects_drop_credential_headers()
    {
        var inner = new ScriptedHandler(request => request.RequestUri!.Host == "a.example"
            ? Redirect(HttpStatusCode.Found, "https://b.example/landing")
            : new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(new SafeRedirectHandler(inner));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://a.example/start") { Content = new StringContent("{}") };
        request.Headers.Add("X-Api-Key", "secret");
        request.Headers.Add("Authorization", "Bearer secret");
        request.Headers.Add("Accept", "application/json");

        var response = await client.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var followed = inner.Requests[1];
        followed.Uri.Should().Be(new Uri("https://b.example/landing"));
        followed.Method.Should().Be(HttpMethod.Get, "302 改用 GET");
        followed.Headers.Should().ContainKey("Accept").And.NotContainKeys("X-Api-Key", "Authorization");
        followed.HasContent.Should().BeFalse();
    }

    [Fact]
    public async Task Same_origin_307_keeps_headers_method_and_body()
    {
        var inner = new ScriptedHandler(request => request.RequestUri!.AbsolutePath == "/old"
            ? Redirect(HttpStatusCode.TemporaryRedirect, "/new")
            : new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(new SafeRedirectHandler(inner));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://a.example/old") { Content = new StringContent("payload") };
        request.Headers.Add("X-Api-Key", "secret");

        await client.SendAsync(request, Ct);

        var followed = inner.Requests[1];
        followed.Uri.Should().Be(new Uri("https://a.example/new"));
        followed.Method.Should().Be(HttpMethod.Post);
        followed.Headers.Should().ContainKey("X-Api-Key");
        followed.Body.Should().Be("payload");
    }

    [Fact]
    public async Task Downgrades_and_redirect_loops_stop()
    {
        var downgrade = new ScriptedHandler(_ => Redirect(HttpStatusCode.Found, "http://a.example/plain"));
        using (var client = new HttpClient(new SafeRedirectHandler(downgrade)))
        {
            (await client.GetAsync(new Uri("https://a.example/"), Ct)).StatusCode.Should().Be(HttpStatusCode.Found);
            downgrade.Requests.Should().ContainSingle("https 不降级到 http");
        }

        var loop = new ScriptedHandler(_ => Redirect(HttpStatusCode.Found, "/again"));
        using (var client = new HttpClient(new SafeRedirectHandler(loop)))
        {
            (await client.GetAsync(new Uri("https://a.example/"), Ct)).StatusCode.Should().Be(HttpStatusCode.Found);
            loop.Requests.Should().HaveCount(6, "最多跟随 5 次");
        }
    }

    private static HttpResponseMessage Redirect(HttpStatusCode status, string location)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed record SeenRequest(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, bool HasContent, string? Body);

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<SeenRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new SeenRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase),
                request.Content is not null,
                body));
            return respond(request);
        }
    }
}
