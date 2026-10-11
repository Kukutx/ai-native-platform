using AiNative.Application.Abstractions;
using AiNative.Application.Agents;
using AiNative.Application.Agents.Runtime;
using AiNative.Application.Ai;
using AiNative.Application.Platform;
using AiNative.Application.Work;
using AiNative.Infrastructure.Agents;
using AiNative.Infrastructure.Ai;
using AiNative.Infrastructure.Dispatching;
using AiNative.Infrastructure.Persistence;
using AiNative.Infrastructure.Security;
using AiNative.Infrastructure.Work;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiNative.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Postgres";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"缺少连接字符串 ConnectionStrings:{ConnectionStringName}。");

        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();

        // 持久化
        services.AddScoped<AuditingInterceptor>();
        services.AddDbContext<AppDbContext>((sp, options) =>
        {
            PersistenceSetup.Configure(options, connectionString);
            options.AddInterceptors(sp.GetRequiredService<AuditingInterceptor>());
        });
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddSingleton<IDistributedLock>(new PostgresAdvisoryLock(connectionString));

        // 请求管道：权限 → 校验 → 日志 → 事务 → 处理器（按注册顺序由外到内）
        services.AddScoped<PostCommitActions>();
        services.AddScoped<IPostCommitActions>(sp => sp.GetRequiredService<PostCommitActions>());
        services.AddScoped<IRequestDispatcher, RequestDispatcher>();
        services.AddScoped(typeof(IRequestPipelineBehavior<,>), typeof(AuthorizationBehavior<,>));
        services.AddScoped(typeof(IRequestPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped(typeof(IRequestPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IRequestPipelineBehavior<,>), typeof(TransactionBehavior<,>));

        // 身份
        services.AddOptions<AuthOptions>()
            .BindConfiguration(AuthOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<TokenService>();
        services.AddSingleton<ITokenService>(sp => sp.GetRequiredService<TokenService>());
        services.AddSingleton<IPasswordHasher, PlatformPasswordHasher>();

        // 模型网关
        services.AddOptions<AiOptions>().BindConfiguration(AiOptions.SectionName);
        services.AddSingleton<IChatClientFactory, AnthropicChatClientFactory>();
        services.AddSingleton<IChatClientFactory, OpenAiChatClientFactory>();
        services.AddSingleton<IChatClientFactory, DemoChatClientFactory>();
        services.AddSingleton<IModelGateway, ModelGateway>();

        // 智能体调度
        services.AddOptions<AgentRunOptions>().BindConfiguration(AgentRunOptions.SectionName);
        services.AddOptions<SchedulerOptions>().BindConfiguration(SchedulerOptions.SectionName);
        services.AddSingleton<WakeupSignal>();
        services.AddSingleton<IWakeupSignal>(sp => sp.GetRequiredService<WakeupSignal>());
        services.AddScoped<IWakeupQueue, WakeupQueue>();
        services.AddScoped<IWorkItemNumbers, WorkItemNumbers>();
        services.AddScoped<IWorkItemCheckout, WorkItemCheckout>();
        services.AddHostedService<HeartbeatScheduler>();

        return services;
    }
}
