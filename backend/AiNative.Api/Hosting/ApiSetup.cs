using AiNative.Api.Errors;
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

        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        services.AddCors(options => options.AddPolicy(CorsPolicy, policy => policy
            .WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()));

        services.AddHealthChecks()
            .AddNpgSql(configuration.GetConnectionString(DependencyInjection.ConnectionStringName)!, name: "postgres", tags: ["ready"]);

        services.AddOpenApiDocument(settings =>
        {
            settings.DocumentName = "v1";
            settings.Title = "AI Native Platform API";
            settings.Version = "v1";
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
        app.UseExceptionHandler();
        app.UseSerilogRequestLogging();
        app.UseCors(CorsPolicy);

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
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();
        return app;
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
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync().ConfigureAwait(false);
    }
}
