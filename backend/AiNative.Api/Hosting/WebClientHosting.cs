using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace AiNative.Api.Hosting;

/// <summary>
/// 生产镜像把 Web 工作台的静态导出放在 wwwroot，由 API 在同一个站点提供：
/// 前端用相对地址访问接口、实时通道和 MCP，不需要跨域，也不需要单独的 Web 服务器。
/// 开发环境没有 wwwroot（Web 端用 next dev 单独运行），这里什么也不做。
/// </summary>
internal static class WebClientHosting
{
    /// <summary>
    /// 后端自己的路径：找不到时返回接口的 404，不返回网页。
    /// </summary>
    private static readonly string[] BackendPrefixes = ["/api", "/hubs", "/mcp", "/health", "/swagger"];

    /// <summary>
    /// 构建镜像时预先压缩好的文件类型（见 tools/precompress.mjs），请求时按 Accept-Encoding 直接发送压缩版本。
    /// </summary>
    private static readonly HashSet<string> Compressible = new(StringComparer.OrdinalIgnoreCase) { ".html", ".js", ".css", ".txt", ".json", ".svg" };

    /// <summary>
    /// 优先 br（体积更小），其次 gzip。
    /// </summary>
    private static readonly (string Encoding, string Extension)[] Encodings = [("br", ".br"), ("gzip", ".gz")];

    private const string VariantKey = "webclient.variant";

    public static WebApplication UseWebClient(this WebApplication app)
    {
        var files = app.Environment.WebRootFileProvider;
        if (!files.GetFileInfo("index.html").Exists)
        {
            return app;
        }

        var contentTypes = new FileExtensionContentTypeProvider();

        // 页面、脚本里都有中文，明确声明编码。
        contentTypes.Mappings[".html"] = "text/html; charset=utf-8";
        contentTypes.Mappings[".js"] = "text/javascript; charset=utf-8";
        contentTypes.Mappings[".css"] = "text/css; charset=utf-8";
        contentTypes.Mappings[".txt"] = "text/plain; charset=utf-8";

        // 压缩文件本身的类型无关紧要，发送前会换回原文件的类型。
        contentTypes.Mappings[".br"] = "application/octet-stream";
        contentTypes.Mappings[".gz"] = "application/octet-stream";

        app.Use((context, next) => ResolveAsync(context, files, contentTypes, next));
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            ContentTypeProvider = contentTypes,
            OnPrepareResponse = PrepareResponse,
        });
        return app;
    }

    /// <summary>
    /// 把页面地址映射到导出的文件：/ → index.html，/analytics → analytics.html，/tasks/view → tasks/view.html；
    /// 浏览器支持时换成预压缩版本。找不到的页面返回导出的 404 页。
    /// </summary>
    private static Task ResolveAsync(HttpContext context, IFileProvider files, FileExtensionContentTypeProvider contentTypes, RequestDelegate next)
    {
        var request = context.Request;
        if (!(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            || context.GetEndpoint() is not null
            || BackendPrefixes.Any(prefix => request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return next(context);
        }

        var path = request.Path.Value is { Length: > 1 } value ? value.TrimEnd('/') : "/";
        var hasExtension = Path.HasExtension(path);
        var file = hasExtension
            ? Existing(files, path)
            : path == "/"
                ? Existing(files, "/index.html")
                : Existing(files, $"{path}.html") ?? Existing(files, $"{path}/index.html");
        if (file is null)
        {
            return hasExtension ? NotFoundAsync(context) : NotFoundPageAsync(context, files);
        }

        request.Path = file;
        var extension = Path.GetExtension(file);
        if (Compressible.Contains(extension) && contentTypes.TryGetContentType(file, out var contentType))
        {
            var accepted = request.Headers.AcceptEncoding;
            foreach (var (encoding, suffix) in Encodings)
            {
                if (Accepts(accepted, encoding) && Existing(files, file + suffix) is { } compressed)
                {
                    request.Path = compressed;
                    context.Items[VariantKey] = new Variant(encoding, contentType);
                    break;
                }
            }

            // 同一个地址按 Accept-Encoding 返回不同内容，缓存要按它区分。
            context.Response.Headers.Vary = HeaderNames.AcceptEncoding;
        }

        return next(context);
    }

    private static void PrepareResponse(StaticFileResponseContext ctx)
    {
        var context = ctx.Context;
        var headers = context.Response.Headers;
        if (context.Items[VariantKey] is Variant variant)
        {
            headers.ContentEncoding = variant.Encoding;
            context.Response.ContentType = variant.ContentType;
        }

        // _next/static 下的文件名带内容哈希，可以永久缓存；页面和其他文件每次向服务器确认是否有新版本。
        headers.CacheControl = context.Request.Path.StartsWithSegments("/_next/static", StringComparison.Ordinal)
            ? "public, max-age=31536000, immutable"
            : "no-cache";
        ApplySecurityHeaders(headers);
    }

    private static async Task NotFoundPageAsync(HttpContext context, IFileProvider files)
    {
        var page = files.GetFileInfo("404.html");
        if (!page.Exists)
        {
            await NotFoundAsync(context).ConfigureAwait(false);
            return;
        }

        var response = context.Response;
        response.StatusCode = StatusCodes.Status404NotFound;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength = page.Length;
        response.Headers.CacheControl = "no-cache";
        ApplySecurityHeaders(response.Headers);
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await response.SendFileAsync(page, context.RequestAborted).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 缺失的静态资源直接 404，不落到兜底的登录校验（否则会得到一个令人困惑的 401）。
    /// </summary>
    private static Task NotFoundAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }

    private static void ApplySecurityHeaders(IHeaderDictionary headers)
    {
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    }

    private static string? Existing(IFileProvider files, string path)
        => files.GetFileInfo(path) is { Exists: true, IsDirectory: false } ? path : null;

    private static bool Accepts(StringValues acceptEncoding, string encoding)
    {
        if (!StringWithQualityHeaderValue.TryParseList(acceptEncoding, out var values))
        {
            return false;
        }

        return values.Any(v => StringSegment.Equals(v.Value, encoding, StringComparison.OrdinalIgnoreCase) && v.Quality is null or > 0);
    }

    private sealed record Variant(string Encoding, string ContentType);
}
