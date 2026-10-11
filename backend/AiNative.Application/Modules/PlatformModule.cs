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
}

public interface IModuleCatalog
{
    IReadOnlyList<IPlatformModule> Modules { get; }

    IPlatformModule? Find(string key);

    IReadOnlyList<FeatureDefinition> AllFeatures { get; }

    IReadOnlyList<PlatformEventDefinition> AllEvents { get; }

    PlatformEventDefinition? FindEvent(string key);
}

public sealed class ModuleCatalog(IEnumerable<IPlatformModule> modules) : IModuleCatalog
{
    private readonly IReadOnlyList<IPlatformModule> _modules = [.. modules.OrderBy(m => m.IsCore ? 0 : 1).ThenBy(m => m.Key, StringComparer.Ordinal)];

    public IReadOnlyList<IPlatformModule> Modules => _modules;

    public IPlatformModule? Find(string key) => _modules.FirstOrDefault(m => m.Key == key);

    public IReadOnlyList<FeatureDefinition> AllFeatures => [.. _modules.SelectMany(m => m.Features)];

    public IReadOnlyList<PlatformEventDefinition> AllEvents => [.. _modules.SelectMany(m => m.Events)];

    public PlatformEventDefinition? FindEvent(string key) => _modules.SelectMany(m => m.Events).FirstOrDefault(e => e.Key == key);
}

/// <summary>
/// 当前租户的模块启用状态（按请求缓存）。
/// </summary>
public interface IModuleAccess
{
    Task<bool> IsEnabledAsync(string moduleKey, CancellationToken cancellationToken = default);

    Task<IReadOnlySet<string>> GetEnabledAsync(CancellationToken cancellationToken = default);
}
