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

        services.AddScoped<WorkItemWorkflow>();
        services.AddScoped<AgentPromptBuilder>();
        services.AddScoped<IAgentRuntime, AgentRuntime>();
        services.AddScoped<IChatService, ChatService>();
        services.AddScoped<ChatTurnRunner>();

        services.AddPlatformModuleAssembly(assembly);
        return services;
    }

    /// <summary>
    /// 外部程序集（新业务模块）复用同一套约定注册自己的处理器、校验器、动作与模块。
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

            if (typeof(IPlatformAction).IsAssignableFrom(type))
            {
                services.AddScoped(typeof(IPlatformAction), type);
            }

            if (typeof(IPlatformModule).IsAssignableFrom(type))
            {
                services.AddSingleton(typeof(IPlatformModule), type);
            }
        }

        return services;
    }
}
