using AiNative.Application.Common;
using AiNative.Application.Tenancy;

namespace AiNative.Application.Access;

/// <summary>
/// 授权上限：给角色、成员或智能体新增的权限必须是操作者自己拥有的，权限只能向下传递、不能被放大。
/// 已有的权限保持不变（调整角色名称或去掉权限不受限制）。
/// </summary>
public static class GrantCeiling
{
    /// <param name="requested">授予后的完整权限集合。</param>
    /// <param name="existing">授予前已有的权限，不在检查范围内。</param>
    public static void Ensure(ITenantContext tenant, IEnumerable<string> requested, IEnumerable<string>? existing = null)
    {
        var current = existing?.ToHashSet(StringComparer.Ordinal) ?? [];
        var beyond = requested
            .Where(feature => !current.Contains(feature) && !FeatureMatcher.IsGranted(tenant.Features, feature))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (beyond.Count > 0)
        {
            throw new ForbiddenException($"不能授予自己没有的权限：{string.Join("、", beyond)}。", "access.grant_exceeds_own");
        }
    }

    /// <summary>
    /// 只保留操作者能授予的权限，用于套用默认权限的场景。
    /// </summary>
    public static IEnumerable<string> Grantable(ITenantContext tenant, IEnumerable<string> features)
        => features.Where(feature => FeatureMatcher.IsGranted(tenant.Features, feature));
}
