using AiNative.Application.Access;

namespace AiNative.Application.Modules;

/// <summary>
/// 业务模块描述。模块是平台扩展的基本单位：声明自己的功能权限、动作（供界面、智能体、HTTP、MCP 共用），
/// 并可按租户启用或停用。核心模块不可停用。
/// </summary>
public interface IPlatformModule
{
    string Key { get; }

    string Name { get; }

    string Description { get; }

    bool IsCore { get; }

    bool EnabledByDefault { get; }

    IReadOnlyList<FeatureDefinition> Features { get; }

    /// <summary>
    /// 模块发布的平台事件，工作流可以订阅它们作为触发器。
    /// </summary>
    IReadOnlyList<PlatformEventDefinition> Events => [];

    /// <summary>
    /// 依赖的模块。依赖没启用时本模块也视为停用；启用本模块前要先启用依赖，停用依赖前要先停用本模块。
    /// </summary>
    IReadOnlyList<string> DependsOn => [];
}

/// <summary>
/// 新空间创建时补充模块自己的初始数据（例如 CRM 的默认销售阶段），与注册在同一事务提交。
/// 实现类放进已注册的程序集即可被扫描。
/// </summary>
public interface ITenantInitializer
{
    Task InitializeAsync(Guid tenantId, CancellationToken cancellationToken);
}

public interface IModuleCatalog
{
    IReadOnlyList<IPlatformModule> Modules { get; }

    IPlatformModule? Find(string key);

    IReadOnlyList<FeatureDefinition> AllFeatures { get; }

    IReadOnlyList<PlatformEventDefinition> AllEvents { get; }

    PlatformEventDefinition? FindEvent(string key);

    /// <summary>
    /// 按租户的启用设置算出实际启用的模块：核心模块总是启用；其他模块看设置（没设置过按默认），且依赖的模块都要实际启用。
    /// </summary>
    IReadOnlySet<string> ResolveEnabled(IReadOnlyDictionary<string, bool> overrides);
}

public sealed class ModuleCatalog(IEnumerable<IPlatformModule> modules) : IModuleCatalog
{
    private readonly IReadOnlyList<IPlatformModule> _modules = [.. modules.OrderBy(m => m.IsCore ? 0 : 1).ThenBy(m => m.Key, StringComparer.Ordinal)];

    public IReadOnlyList<IPlatformModule> Modules => _modules;

    public IPlatformModule? Find(string key) => _modules.FirstOrDefault(m => m.Key == key);

    public IReadOnlyList<FeatureDefinition> AllFeatures => [.. _modules.SelectMany(m => m.Features)];

    public IReadOnlyList<PlatformEventDefinition> AllEvents => [.. _modules.SelectMany(m => m.Events)];

    public PlatformEventDefinition? FindEvent(string key) => _modules.SelectMany(m => m.Events).FirstOrDefault(e => e.Key == key);

    public IReadOnlySet<string> ResolveEnabled(IReadOnlyDictionary<string, bool> overrides)
    {
        var enabled = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in _modules)
        {
            Resolve(module);
        }

        return enabled;

        bool Resolve(IPlatformModule module)
        {
            if (enabled.Contains(module.Key))
            {
                return true;
            }

            // 循环依赖视为未启用，避免无限递归。
            if (!visiting.Add(module.Key))
            {
                return false;
            }

            var on = module.IsCore
                     || (overrides.GetValueOrDefault(module.Key, module.EnabledByDefault)
                         && module.DependsOn.All(key => Find(key) is { } dependency && Resolve(dependency)));
            visiting.Remove(module.Key);
            if (on)
            {
                enabled.Add(module.Key);
            }

            return on;
        }
    }
}

/// <summary>
/// 当前租户的模块启用状态（按请求缓存）。
/// </summary>
public interface IModuleAccess
{
    Task<bool> IsEnabledAsync(string moduleKey, CancellationToken cancellationToken = default);

    Task<IReadOnlySet<string>> GetEnabledAsync(CancellationToken cancellationToken = default);
}
