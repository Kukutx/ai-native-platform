using AiNative.Application.Abstractions;
using AiNative.Application.Agents;
using AiNative.Application.Agents.Runtime;
using AiNative.Application.Ai;
using AiNative.Application.Erp;
using AiNative.Application.Knowledge;
using AiNative.Application.Platform;
using AiNative.Application.Work;
using AiNative.Application.Workflows;
using AiNative.Infrastructure.Agents;
using AiNative.Infrastructure.Ai;
using AiNative.Infrastructure.Dispatching;
using AiNative.Infrastructure.Erp;
using AiNative.Infrastructure.Knowledge;
using AiNative.Infrastructure.Persistence;
using AiNative.Infrastructure.Security;
using AiNative.Infrastructure.Work;
using AiNative.Infrastructure.Workflows;
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
        services.AddScoped<ITransactionLock, PostgresTransactionLock>();

        // 请求管道：权限 → 校验 → 日志 → 事务 → 处理器（按注册顺序由外到内）
        services.AddScoped<PostCommitActions>();
        services.AddScoped<IPostCommitActions>(sp => sp.GetRequiredService<PostCommitActions>());
        services.AddScoped<IUnitOfWork, ScopedUnitOfWork>();
        services.AddSingleton<ITextMatcher, PostgresTextMatcher>();
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

        // 知识与记忆：向量化、混合检索、后台补齐向量
        services.AddSingleton<IEmbeddingService, EmbeddingService>();
        services.AddScoped<IKnowledgeSearch, KnowledgeSearch>();
        services.AddSingleton<KnowledgeIndexSignal>();
        services.AddSingleton<IKnowledgeIndexSignal>(sp => sp.GetRequiredService<KnowledgeIndexSignal>());
        services.AddHostedService<KnowledgeIndexer>();

        // 智能体调度
        services.AddOptions<AgentRunOptions>().BindConfiguration(AgentRunOptions.SectionName);
        services.AddOptions<SchedulerOptions>().BindConfiguration(SchedulerOptions.SectionName);
        services.AddSingleton<WakeupSignal>();
        services.AddSingleton<IWakeupSignal>(sp => sp.GetRequiredService<WakeupSignal>());
        services.AddScoped<IWakeupQueue, WakeupQueue>();
        services.AddScoped<IWorkItemNumbers, WorkItemNumbers>();
        services.AddScoped<ISequenceAllocator, SequenceAllocator>();
        services.AddScoped<IStockLedger, StockLedger>();
        services.AddScoped<IWorkItemCheckout, WorkItemCheckout>();
        services.AddHostedService<HeartbeatScheduler>();

        // 工作流引擎：定时触发、领取并执行运行
        services.AddOptions<WorkflowOptions>().BindConfiguration(WorkflowOptions.SectionName);
        services.AddSingleton<WorkflowSignal>();
        services.AddSingleton<IWorkflowSignal>(sp => sp.GetRequiredService<WorkflowSignal>());
        services.AddHostedService<WorkflowEngine>();

        return services;
    }
}
