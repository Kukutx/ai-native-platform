using System.Reflection;
using AiNative.Application.Abstractions;
using AiNative.Application.Actions;
using AiNative.Application.Agents.Runtime;
using AiNative.Application.Chat;
using AiNative.Application.Modules;
using AiNative.Application.Platform;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Application.Work;
using AiNative.Application.Workflows;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace AiNative.Application;

public static class DependencyInjection
{
    /// <summary>
    /// 注册应用层：请求处理器、校验器、平台动作与模块按约定扫描注册，新增功能无需改这里。
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddSingleton<ITenantScopeFactory, TenantScopeFactory>();

        services.AddSingleton<IModuleCatalog, ModuleCatalog>();
        services.AddScoped<IModuleAccess, ModuleAccess>();
        services.AddScoped<IAccessResolver, AccessResolver>();
        services.AddScoped<IApiKeyAuthenticator, ApiKeyAuthenticator>();
        services.AddScoped<IActionInvoker, ActionInvoker>();
        services.AddScoped<IChangeFeed, ChangeFeed>();
        services.AddScoped<TenantCalendar>();

        services.AddScoped<WorkItemWorkflow>();
        services.AddScoped<Crm.CrmRecords>();
        services.AddScoped<Crm.CrmCustomFields>();
        services.AddScoped<Erp.ErpRecords>();
        services.AddScoped<Erp.Actions.ErpRefs>();
        services.AddScoped<Projects.ProjectRecords>();
        services.AddScoped<Projects.ProjectPlacement>();
        services.AddScoped<Projects.ProjectWorkflow>();
        services.AddScoped<Projects.Actions.ProjectRefs>();
        services.AddScoped<Analytics.MetricCatalog>();
        services.AddScoped<Analytics.MetricLabels>();
        services.AddScoped<Analytics.AnalyticsEngine>();
        services.AddScoped<Integrations.ITenantSecrets, Integrations.TenantSecrets>();
        services.AddScoped<Integrations.SecretReferences>();
        services.AddScoped<Integrations.McpServerTools>();
        services.AddScoped<AgentPromptBuilder>();
        services.AddScoped<IAgentRuntime, AgentRuntime>();
        services.AddScoped<IChatService, ChatService>();
        services.AddScoped<ChatTurnRunner>();

        services.AddScoped<WorkflowExecutionScope>();
        services.AddScoped<IPlatformEvents, WorkflowTriggers>();
        services.AddScoped<WorkflowDefinitionValidator>();
        services.AddScoped<WorkflowOwnerAccess>();
        services.AddScoped<IWorkflowExecutor, WorkflowExecutor>();
        services.AddScoped<IWorkflowScheduleTrigger, WorkflowScheduleTrigger>();

        services.AddPlatformModuleAssembly(assembly);
        return services;
    }

    /// <summary>
    /// 外部程序集（新业务模块）复用同一套约定注册自己的处理器、校验器、动作、指标、模块与新空间初始化。
    /// </summary>
    public static IServiceCollection AddPlatformModuleAssembly(this IServiceCollection services, Assembly assembly)
    {
        foreach (var type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false }))
        {
            foreach (var contract in type.GetInterfaces().Where(i => i.IsGenericType))
            {
                var definition = contract.GetGenericTypeDefinition();
                if (definition == typeof(IRequestHandler<,>) || definition == typeof(IValidator<>))
                {
                    services.AddScoped(contract, type);
                }
            }

            if (typeof(IPlatformAction).IsAssignableFrom(type) && !typeof(IDynamicPlatformAction).IsAssignableFrom(type))
            {
                services.AddScoped(typeof(IPlatformAction), type);
            }

            if (typeof(IDynamicActionSource).IsAssignableFrom(type))
            {
                services.AddScoped(typeof(IDynamicActionSource), type);
            }

            if (typeof(Analytics.IPlatformMetric).IsAssignableFrom(type))
            {
                services.AddScoped(typeof(Analytics.IPlatformMetric), type);
            }

            if (typeof(IPlatformModule).IsAssignableFrom(type))
            {
                services.AddSingleton(typeof(IPlatformModule), type);
            }

            if (typeof(ITenantInitializer).IsAssignableFrom(type))
            {
                services.AddScoped(typeof(ITenantInitializer), type);
            }
        }

        return services;
    }
}
