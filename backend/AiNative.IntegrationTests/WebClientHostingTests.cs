using System.Net;
using System.Text;
using AiNative.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiNative.IntegrationTests;

/// <summary>
/// 生产镜像里 API 同站点提供 Web 工作台的静态导出（wwwroot）。
/// </summary>
public sealed class WebClientHostingTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private readonly string _webRoot = Directory.CreateTempSubdirectory("ainative-web-").FullName;
    private readonly byte[] _script = Encoding.UTF8.GetBytes(new string('x', 4096));

    [Fact]
    public async Task Serves_exported_pages_with_clean_urls()
    {
        using var client = WebClient();

        var home = await client.GetAsync(new Uri("/", UriKind.Relative), Ct);
        home.StatusCode.Should().Be(HttpStatusCode.OK);
        home.Content.Headers.ContentType!.ToString().Should().Be("text/html; charset=utf-8");
        (await home.Content.ReadAsStringAsync(Ct)).Should().Be("首页");
        home.Headers.CacheControl!.NoCache.Should().BeTrue();
        home.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        home.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");

        (await TextAsync(client, "/analytics")).Should().Be("经营分析");
        (await TextAsync(client, "/analytics/")).Should().Be("经营分析");
        (await TextAsync(client, "/analytics/explore")).Should().Be("自由分析");
        (await TextAsync(client, "/tasks/view?id=0193")).Should().Be("任务详情");
        (await TextAsync(client, "/settings")).Should().Be("设置");

        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/analytics"), Ct);
        head.StatusCode.Should().Be(HttpStatusCode.OK);
        head.Content.Headers.ContentLength.Should().Be(Encoding.UTF8.GetByteCount("经营分析"));
    }

    [Fact]
    public async Task Sends_precompressed_assets_and_caches_hashed_files_forever()
    {
        using var client = WebClient();

        var br = await AssetAsync(client, "br, gzip;q=0.8");
        br.Content.Headers.ContentEncoding.Should().Equal("br");
        br.Content.Headers.ContentType!.ToString().Should().Be("text/javascript; charset=utf-8");
        (await br.Content.ReadAsByteArrayAsync(Ct)).Should().Equal("br"u8.ToArray());
        br.Headers.CacheControl!.ToString().Should().Be("public, max-age=31536000, immutable");
        br.Headers.Vary.Should().Contain("Accept-Encoding");

        var gzip = await AssetAsync(client, "gzip, br;q=0");
        gzip.Content.Headers.ContentEncoding.Should().Equal("gzip");
        (await gzip.Content.ReadAsByteArrayAsync(Ct)).Should().Equal("gz"u8.ToArray());

        var plain = await AssetAsync(client, null);
        plain.Content.Headers.ContentEncoding.Should().BeEmpty();
        (await plain.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(_script);
        plain.Headers.Vary.Should().Contain("Accept-Encoding");

        // 带 ETag 的条件请求得到 304，不重复传输。
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/_next/static/chunks/app.js");
        conditional.Headers.IfNoneMatch.Add(plain.Headers.ETag!);
        (await client.SendAsync(conditional, Ct)).StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task Missing_pages_get_the_exported_404_page_and_backend_paths_stay_untouched()
    {
        using var client = WebClient();

        var missing = await client.GetAsync(new Uri("/no-such-page", UriKind.Relative), Ct);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await missing.Content.ReadAsStringAsync(Ct)).Should().Be("页面不存在");
        missing.Content.Headers.ContentType!.MediaType.Should().Be("text/html");

        var missingAsset = await client.GetAsync(new Uri("/_next/static/chunks/gone.js", UriKind.Relative), Ct);
        missingAsset.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await missingAsset.Content.ReadAsByteArrayAsync(Ct)).Should().BeEmpty();

        // 后端路径照旧：未登录访问接口是 401，健康检查可用，接口不存在也不会返回网页。
        (await client.GetAsync(new Uri("/api/work-items", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync(new Uri("/health/live", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var unknownApi = await client.GetAsync(new Uri("/api/no-such-endpoint", UriKind.Relative), Ct);
        unknownApi.Content.Headers.ContentType?.MediaType.Should().NotBe("text/html");

        // 写请求不由静态页处理。
        var post = await client.PostAsync(new Uri("/", UriKind.Relative), new StringContent(string.Empty), Ct);
        post.StatusCode.Should().NotBe(HttpStatusCode.OK);

        // 登录、注册仍然走接口。
        var auth = await RegisterAsync();
        auth.AccessToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Without_web_root_nothing_is_served()
    {
        using var client = Client();
        var response = await client.GetAsync(new Uri("/", UriKind.Relative), Ct);
        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(_webRoot, recursive: true);
    }

    private HttpClient WebClient()
    {
        Write("index.html", "首页");
        Write("analytics.html", "经营分析");
        Write("analytics/explore.html", "自由分析");
        Write("tasks/view.html", "任务详情");
        Write("settings.html", "设置");
        Write("404.html", "页面不存在");
        Write("_next/static/chunks/app.js", _script);
        Write("_next/static/chunks/app.js.br", "br"u8.ToArray());
        Write("_next/static/chunks/app.js.gz", "gz"u8.ToArray());

        return Factory
            .WithWebHostBuilder(builder => builder.UseSetting(WebHostDefaults.WebRootKey, _webRoot))
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private void Write(string path, string content) => Write(path, Encoding.UTF8.GetBytes(content));

    private void Write(string path, byte[] content)
    {
        var full = Path.Combine(_webRoot, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }

    private static async Task<string> TextAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative), Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, url);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    private static async Task<HttpResponseMessage> AssetAsync(HttpClient client, string? acceptEncoding)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/_next/static/chunks/app.js");
        if (acceptEncoding is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        }

        var response = await client.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return response;
    }
}
