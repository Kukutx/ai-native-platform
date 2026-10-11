using System.Security.Claims;
using AiNative.Application.Platform;

namespace AiNative.Api.Security;

/// <summary>
/// 令牌里携带的身份：用户、当前空间（租户）、成员关系。
/// </summary>
public readonly record struct PlatformPrincipal(Guid UserId, Guid TenantId, Guid MembershipId)
{
    public static PlatformPrincipal? From(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return Guid.TryParse(user.FindFirstValue(PlatformClaims.UserId), out var userId)
               && Guid.TryParse(user.FindFirstValue(PlatformClaims.TenantId), out var tenantId)
               && Guid.TryParse(user.FindFirstValue(PlatformClaims.MembershipId), out var membershipId)
            ? new PlatformPrincipal(userId, tenantId, membershipId)
            : null;
    }
}
