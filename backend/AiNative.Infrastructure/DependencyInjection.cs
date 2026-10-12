using System.Data.Common;
using AiNative.Application.Abstractions;
using AiNative.Application.Agents;
using AiNative.Application.Agents.Runtime;
using AiNative.Application.Ai;
using AiNative.Application.Crm;
using AiNative.Application.Erp;
using AiNative.Application.Files;
using AiNative.Application.Integrations;
using AiNative.Application.Knowledge;
using AiNative.Application.Platform;
using AiNative.Application.Work;
using AiNative.Application.Workflows;
using AiNative.Infrastructure.Agents;
using AiNative.Infrastructure.Common;
using AiNative.Infrastructure.Ai;
using AiNative.Infrastructure.Cluster;
using AiNative.Infrastructure.Dispatching;
using AiNative.Infrastructure.Crm;
using AiNative.Infrastructure.Erp;
using AiNative.Infrastructure.Files;
using AiNative.Infrastructure.Integrations;
using AiNative.Infrastructure.Knowledge;
using AiNative.Infrastructure.Knowledge.Files;
using AiNative.Infrastructure.Persistence;
using AiNative.Infrastructure.Security;
using AiNative.Infrastructure.Work;
using AiNative.Infrastructure.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AiNative.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Postgres";

    /// <summary>
    /// 读取数据库连接字符串。没有显式配置 GSS Encryption Mode 时关闭 GSS 加密协商：
    /// Linux 容器镜像不带 Kerberos 库，Npgsql 默认的协商会在每次建连时打印「无法加载 libgssapi_krb5」的错误。
    /// 确实要用 GSS 加密的部署在连接字符串里写明即可。
    /// </summary>
    public static string GetPlatformConnectionString(this IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"缺少数据库连接字符串 ConnectionStrings:{ConnectionStringName}（环境变量 ConnectionStrings__{ConnectionStringName}）。");
        }

        var keys = new DbConnectionStringBuilder { ConnectionString = connectionString }.Keys.Cast<string>();
        if (keys.Any(key => key.Replace(" ", string.Empty, StringComparison.Ordinal).Equals("GssEncryptionMode", StringComparison.OrdinalIgnoreCase)))
        {
            return connectionString;
        }

        return new NpgsqlConnectionStringBuilder(connectionString) { GssEncryptionMode = GssEncryptionMode.Disable }.ConnectionString;
    }

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetPlatformConnectionString();

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

        // 集群协调：实例登记与广播最先启动、最后停止（后台服务按注册的逆序停止），
        // 调度器停止时放回队列的运行还能通知到其他实例，注销也在调度器停下之后。
        services.AddOptions<ClusterOptions>()
            .BindConfiguration(ClusterOptions.SectionName)
            .Validate(o => o.HeartbeatInterval > TimeSpan.Zero && o.InstanceTimeout >= o.HeartbeatInterval * 3, "Cluster:InstanceTimeout 至少要是 Cluster:HeartbeatInterval 的三倍。")
            .ValidateOnStart();
        services.AddSingleton<ClusterNode>();
        services.AddSingleton<IClusterNode>(sp => sp.GetRequiredService<ClusterNode>());
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<PostgresClusterBus>(sp, connectionString));
        services.AddSingleton<IClusterBus>(sp => sp.GetRequiredService<PostgresClusterBus>());
        services.AddHostedService(sp => sp.GetRequiredService<PostgresClusterBus>());
        services.AddHostedService<ClusterMembership>();

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
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>();
        services.AddOptions<RegistrationOptions>().BindConfiguration(RegistrationOptions.SectionName);
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

        // 知识库上传文件的解析器（按扩展名选择）。
        services.AddSingleton<IKnowledgeFileExtractor, PlainTextExtractor>();
        services.AddSingleton<IKnowledgeFileExtractor, HtmlExtractor>();
        services.AddSingleton<IKnowledgeFileExtractor, PdfExtractor>();
        services.AddSingleton<IKnowledgeFileExtractor, WordExtractor>();
        services.AddSingleton<IKnowledgeFileExtractor, SpreadsheetExtractor>();
        services.AddSingleton<IKnowledgeFileExtractor, PresentationExtractor>();
        services.AddSingleton<ITabularFileReader, TabularFileReader>();

        // 智能体调度
        services.AddOptions<AgentRunOptions>().BindConfiguration(AgentRunOptions.SectionName);
        services.AddOptions<SchedulerOptions>().BindConfiguration(SchedulerOptions.SectionName);
        services.AddSingleton<WakeupSignal>();
        services.AddSingleton<IWakeupSignal, BroadcastWakeupSignal>();
        services.AddSingleton<IClusterMessageHandler>(sp => new SignalRelay(ClusterTopics.WakeupQueued, sp.GetRequiredService<WakeupSignal>()));
        services.AddScoped<IWakeupQueue, WakeupQueue>();
        services.AddScoped<IWorkItemNumbers, WorkItemNumbers>();
        services.AddScoped<ISequenceAllocator, SequenceAllocator>();
        services.AddScoped<IStockLedger, StockLedger>();
        services.AddScoped<ICrmCustomFieldStore, CrmCustomFieldStore>();
        services.AddScoped<IWorkItemCheckout, WorkItemCheckout>();
        services.AddScoped<IWorkItemGraph, WorkItemGraph>();
        services.AddSingleton<AgentRunRecovery>();
        services.AddHostedService<HeartbeatScheduler>();

        // 工作流引擎：定时触发、领取并执行运行
        services.AddOptions<WorkflowOptions>().BindConfiguration(WorkflowOptions.SectionName);
        services.AddSingleton<WorkflowSignal>();
        services.AddSingleton<IWorkflowSignal, BroadcastWorkflowSignal>();
        services.AddSingleton<IClusterMessageHandler>(sp => new SignalRelay(ClusterTopics.WorkflowQueued, sp.GetRequiredService<WorkflowSignal>()));
        services.AddOptions<OutboundNetworkOptions>().BindConfiguration(OutboundNetworkOptions.SectionName);
        services.AddSingleton<IWorkflowHttpClient, WorkflowHttpClient>();
        services.AddSingleton<WorkflowRunRecovery>();
        services.AddHostedService<WorkflowEngine>();

        // 集成：空间密钥加密、外部 MCP 服务
        services.AddOptions<SecretsOptions>()
            .BindConfiguration(SecretsOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SecretsOptions>, SecretsOptionsValidator>();
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
        services.AddOptions<IntegrationOptions>().BindConfiguration(IntegrationOptions.SectionName);
        services.AddSingleton<IMcpGateway, McpGateway>();

        return services;
    }
}
