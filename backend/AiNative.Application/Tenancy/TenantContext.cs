using AiNative.Application.Access;
using AiNative.Application.Common;
using AiNative.Domain.Common;

namespace AiNative.Application.Tenancy;

/// <summary>
/// 当前请求（或后台执行）的身份上下文：属于哪个租户、谁在操作、拥有哪些功能权限。
/// HTTP 请求由认证中间件填充；智能体运行、实时通道由各自入口在新作用域里填充。
/// </summary>
public interface ITenantContext
{
    bool HasTenant { get; }

    /// <summary>
    /// 当前租户；未进入租户上下文时抛出，避免意外写出无主数据。
    /// </summary>
    Guid TenantId { get; }

    /// <summary>
    /// 供查询过滤器使用：未进入租户时返回空 Guid，从而什么都查不到。
    /// </summary>
    Guid TenantIdOrEmpty { get; }

    Actor Actor { get; }

    Guid? MembershipId { get; }

    IReadOnlyCollection<string> Features { get; }

    bool Can(string feature);

    void Demand(string feature);
}

public sealed class TenantContext : ITenantContext
{
    private Guid? _tenantId;
    private string[] _features = [];

    public bool HasTenant => _tenantId.HasValue;

    public Guid TenantId => _tenantId ?? throw new UnauthorizedException("当前操作不在任何租户上下文中。");

    public Guid TenantIdOrEmpty => _tenantId ?? Guid.Empty;

    public Actor Actor { get; private set; } = Actor.System;

    public Guid? MembershipId { get; private set; }

    public IReadOnlyCollection<string> Features => _features;

    public bool Can(string feature) => FeatureMatcher.IsGranted(_features, feature);

    public void Demand(string feature)
    {
        if (!Can(feature))
        {
            throw new ForbiddenException($"缺少权限 {feature}。");
        }
    }

    public void Enter(Guid tenantId, Actor actor, IEnumerable<string> features, Guid? membershipId = null)
    {
        _tenantId = tenantId;
        Actor = actor;
        MembershipId = membershipId;
        _features = [.. features.Distinct(StringComparer.Ordinal)];
    }

    public void Reset()
    {
        _tenantId = null;
        Actor = Actor.System;
        MembershipId = null;
        _features = [];
    }
}
