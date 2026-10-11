using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Modules;
using AiNative.Application.Tenancy;
using AiNative.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AiNative.Application.Platform;

public sealed record ResolvedAccess(Guid TenantId, Guid UserId, Guid MembershipId, bool IsOwner, IReadOnlyList<string> Features);

/// <summary>
/// 把令牌里的身份解析成当前权限：成员身份必须有效，权限 = 各角色功能键的并集，所有者拥有全部权限。
/// 结果短暂缓存，角色调整在几秒内生效。
/// </summary>
public interface IAccessResolver
{
    Task<ResolvedAccess?> ResolveAsync(Guid userId, Guid tenantId, Guid membershipId, CancellationToken cancellationToken = default);
}

internal sealed class AccessResolver(IAppDbContext db, IMemoryCache cache) : IAccessResolver
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    public async Task<ResolvedAccess?> ResolveAsync(Guid userId, Guid tenantId, Guid membershipId, CancellationToken cancellationToken = default)
    {
        var key = $"access:{membershipId}";
        if (cache.TryGetValue(key, out ResolvedAccess? cached) && cached is not null && cached.UserId == userId && cached.TenantId == tenantId)
        {
            return cached;
        }

        var membership = await db.Memberships.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == membershipId && m.UserId == userId && m.TenantId == tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (membership is null || membership.Status != MembershipStatus.Active)
        {
            return null;
        }

        IReadOnlyList<string> features;
        if (membership.IsOwner)
        {
            features = [FeatureMatcher.All];
        }
        else
        {
            var roleIds = membership.RoleIds;
            var roleFeatures = await db.Roles.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.TenantId == tenantId && roleIds.Contains(r.Id))
                .Select(r => r.Features)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            features = [.. roleFeatures.SelectMany(f => f).Distinct(StringComparer.Ordinal)];
        }

        var access = new ResolvedAccess(tenantId, userId, membershipId, membership.IsOwner, features);
        cache.Set(key, access, CacheDuration);
        return access;
    }
}

/// <summary>
/// 当前租户的模块启用状态，作用域内只查一次。
/// </summary>
internal sealed class ModuleAccess(IAppDbContext db, IModuleCatalog catalog, ITenantContext tenant) : IModuleAccess
{
    private IReadOnlySet<string>? _enabled;
    private Guid _loadedFor;

    public async Task<bool> IsEnabledAsync(string moduleKey, CancellationToken cancellationToken = default)
        => (await GetEnabledAsync(cancellationToken).ConfigureAwait(false)).Contains(moduleKey);

    public async Task<IReadOnlySet<string>> GetEnabledAsync(CancellationToken cancellationToken = default)
    {
        if (!tenant.HasTenant)
        {
            return new HashSet<string>(catalog.Modules.Where(m => m.IsCore).Select(m => m.Key));
        }

        if (_enabled is not null && _loadedFor == tenant.TenantId)
        {
            return _enabled;
        }

        var overrides = await db.TenantModules.AsNoTracking()
            .ToDictionaryAsync(m => m.ModuleKey, m => m.Enabled, cancellationToken).ConfigureAwait(false);
        _enabled = catalog.Modules
            .Where(m => m.IsCore || overrides.GetValueOrDefault(m.Key, m.EnabledByDefault))
            .Select(m => m.Key)
            .ToHashSet(StringComparer.Ordinal);
        _loadedFor = tenant.TenantId;
        return _enabled;
    }
}
