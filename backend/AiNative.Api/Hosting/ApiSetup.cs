using System.Threading.RateLimiting;
using AiNative.Api.Errors;
using AiNative.Api.Mcp;
using AiNative.Api.Realtime;
using AiNative.Api.Security;
using AiNative.Application.Abstractions;
using AiNative.Application.Common;
using AiNative.Application.Realtime;
using AiNative.Infrastructure;
using AiNative.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NSwag;
using NSwag.Generation.Processors.Security;
using Serilog;

namespace AiNative.Api.Hosting;

internal static class ApiSetup
{
    private const string CorsPolicy = "clients";

    /// <summary>
    /// 登录、注册、修改密码的频率限制：按客户端 IP 每分钟最多 Auth:RateLimitPerMinute 次（0 表示不限）。
    /// 部署在反向代理后面时需要打开 ASPNETCORE_FORWARDEDHEADERS_ENABLED，否则所有人共用代理的 IP。
    /// </summary>
    public const string AuthRateLimitPolicy = "auth";

    public static IServiceCollection AddPlatformApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddControllers(options => options.ModelBinderProviders.Insert(0, new SnakeCaseEnumModelBinderProvider()))
            .AddJsonOptions(options => PlatformJson.Apply(options.JsonSerializerOptions))
            .ConfigureApiBehaviorOptions(options =>
            {
                var defaultFactory = options.InvalidModelStateResponseFactory;
                options.InvalidModelStateResponseFactory = context =>
                {
                    var result = defaultFactory(context);
                    if (result is ObjectResult { Value: ProblemDetails details })
                    {
                        details.Extensions["code"] = "validation_failed";
                    }

                    return result;
                };
            });
        services.ConfigureHttpJsonOptions(options => PlatformJson.Apply(options.SerializerOptions));
        services.AddProblemDetails();
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddRateLimiter(options =>
        {
            var perMinute = configuration.GetValue("Auth:RateLimitPerMinute", 20);
            options.AddPolicy(AuthRateLimitPolicy, context => perMinute <= 0
                ? RateLimitPartition.GetNoLimiter(string.Empty)
                : RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.OnRejected = WriteRateLimitedAsync;
        });

        services.AddAuthentication(TokenAuthenticationHandler.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TokenAuthenticationHandler>(TokenAuthenticationHandler.SchemeName, null);
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        services.AddSignalR(options => options.AddFilter<TenantHubFilter>())
            .AddJsonProtocol(options => PlatformJson.Apply(options.PayloadSerializerOptions));
        services.AddSingleton<TenantHubFilter>();
        services.AddSingleton<SignalRRealtimeNotifier>();
        services.AddSingleton<IRealtimeNotifier, ClusteredRealtimeNotifier>();
        services.AddSingleton<IClusterMessageHandler, RealtimeChangeRelay>();
        services.AddSingleton<IClusterMessageHandler, RealtimeRunEventRelay>();
        services.AddPlatformMcp();

        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        services.AddCors(options => options.AddPolicy(CorsPolicy, policy => policy
            .WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()));

        services.AddHealthChecks()
            .AddNpgSql(configuration.GetPlatformConnectionString(), name: "postgres", tags: ["ready"]);

        services.AddOpenApiDocument(settings =>
        {
            settings.DocumentName = "v1";
            settings.Title = "AI Native Platform API";
            settings.Version = "v1";
            settings.SchemaSettings.SchemaProcessors.Add(new RequiredPropertiesSchemaProcessor());
            settings.AddSecurity(TokenAuthenticationHandler.SchemeName, new OpenApiSecurityScheme
            {
                Type = OpenApiSecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
            });
            settings.OperationProcessors.Add(new AspNetCoreOperationSecurityScopeProcessor(TokenAuthenticationHandler.SchemeName));
        });

        return services;
    }

    public static WebApplication UsePlatformApi(this WebApplication app)
    {
        // 请求日志放在异常处理外层，记录的是转换后的真实状态码：客户端中途取消记为 499，业务错误按 4xx 记，
        // 不会被当成 500 错误刷屏。真正的 500 由异常处理器连同堆栈记录。
        app.UseSerilogRequestLogging();
        app.UseExceptionHandler();
        app.Use(DisableProxyBufferingForStreams);
        app.UseCors(CorsPolicy);
        app.UseRateLimiter();

        // Web 工作台的静态页面（仅生产镜像里有）不需要登录，放在认证之前。
        app.UseWebClient();

        // 接口文档公开可读（兜底授权策略对没有终结点的请求同样生效，所以放在授权之前）。
        app.UseOpenApi();
        if (app.Environment.IsDevelopment())
        {
            app.UseSwaggerUi();
        }

        app.UseAuthentication();
        app.UseMiddleware<TenantContextMiddleware>();
        app.UseAuthorization();

        app.MapControllers();
        app.MapHub<RealtimeHub>("/hubs/realtime");
        app.MapMcp(PlatformMcpServer.Route).ExcludeFromDescription();
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();
        return app;
    }

    private static async ValueTask WriteRateLimitedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var details = new ProblemDetails { Title = "请求太频繁，请稍后再试。", Status = StatusCodes.Status429TooManyRequests };
        details.Extensions["code"] = "rate_limited";
        await http.RequestServices.GetRequiredService<IProblemDetailsService>()
            .WriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = details })
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 流式响应（对话的 SSE、MCP 的事件流）告诉 nginx 等反向代理不要缓冲，否则整段回复结束后才一次性到达客户端。
    /// </summary>
    private static Task DisableProxyBufferingForStreams(HttpContext context, RequestDelegate next)
    {
        context.Response.OnStarting(static state =>
        {
            var response = (HttpResponse)state;
            if (response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
            {
                response.Headers["X-Accel-Buffering"] = "no";
            }

            return Task.CompletedTask;
        }, context.Response);
        return next(context);
    }

    /// <summary>
    /// 开发环境默认启动时自动迁移数据库；生产环境建议在发布流程中单独执行迁移。
    /// </summary>
    public static async Task MigrateDatabaseIfConfiguredAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue("Database:MigrateOnStartup", app.Environment.IsDevelopment()))
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 数据库本身不存在时先建库（例如连接的是一台全新的 PostgreSQL 实例）。多个实例同时启动时只有一个建得成，其余忽略。
        var creator = db.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync().ConfigureAwait(false))
        {
            try
            {
                await creator.CreateAsync().ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DuplicateDatabase)
            {
            }
        }

        // 多个实例同时启动时排队迁移：后到的等前一个迁移完，再发现已经没有待执行的迁移。
        var locks = app.Services.GetRequiredService<IDistributedLock>();
        IAsyncDisposable? migrationLock;
        while ((migrationLock = await locks.TryAcquireAsync("database:migrate").ConfigureAwait(false)) is null)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }

        await using (migrationLock)
        {
            // 再建好迁移历史表：空数据库上 EF 会先查询这张表，失败后才去创建，留下一条看着像故障的错误日志。
            await db.GetService<IHistoryRepository>().CreateIfNotExistsAsync().ConfigureAwait(false);
            await db.Database.MigrateAsync().ConfigureAwait(false);
        }
    }
}
