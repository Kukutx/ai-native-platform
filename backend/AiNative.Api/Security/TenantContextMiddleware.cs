using AiNative.Application.Platform;
using AiNative.Application.Tenancy;
using AiNative.Domain.Common;

namespace AiNative.Api.Security;

/// <summary>
/// 把认证阶段解析出的身份与权限写入本次请求的租户上下文，之后的查询过滤、写入盖章、权限检查都依赖它。
/// </summary>
internal sealed class TenantContextMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context, TenantContext tenant)
    {
        if (context.Features.Get<ResolvedAccess>() is { } access && context.User.Identity?.IsAuthenticated == true)
        {
            tenant.Enter(access.TenantId, Actor.ForUser(access.UserId), access.Features, access.MembershipId);
        }

        return next(context);
    }
}
