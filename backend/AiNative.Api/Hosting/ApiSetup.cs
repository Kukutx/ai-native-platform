using AiNative.Api.Errors;
using AiNative.Api.Mcp;
using AiNative.Api.Realtime;
using AiNative.Api.Security;
using AiNative.Application.Common;
using AiNative.Application.Realtime;
using AiNative.Infrastructure;
using AiNative.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using NSwag;
using NSwag.Generation.Processors.Security;
using Serilog;

namespace AiNative.Api.Hosting;

internal static class ApiSetup
{
    private const string CorsPolicy = "clients";

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

        services.AddAuthentication(TokenAuthenticationHandler.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TokenAuthenticationHandler>(TokenAuthenticationHandler.SchemeName, null);
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        services.AddSignalR(options => options.AddFilter<TenantHubFilter>())
            .AddJsonProtocol(options => PlatformJson.Apply(options.PayloadSerializerOptions));
        services.AddSingleton<TenantHubFilter>();
        services.AddSingleton<IRealtimeNotifier, SignalRRealtimeNotifier>();
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

        // 数据库本身不存在时先建库（例如连接的是一台全新的 PostgreSQL 实例）。
        var creator = db.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync().ConfigureAwait(false))
        {
            await creator.CreateAsync().ConfigureAwait(false);
        }

        // 再建好迁移历史表：空数据库上 EF 会先查询这张表，失败后才去创建，留下一条看着像故障的错误日志。
        await db.GetService<IHistoryRepository>().CreateIfNotExistsAsync().ConfigureAwait(false);
        await db.Database.MigrateAsync().ConfigureAwait(false);
    }
}
