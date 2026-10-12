using System.Security.Claims;
using AiNative.Application.Common;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AiNative.Api.Security;

/// <summary>
/// 只接受登录令牌的接口：API 密钥是给 MCP 客户端和脚本用的受限凭据，
/// 不能用来换发登录令牌、修改密码或管理密钥，否则密钥的权限范围、过期和吊销都会被绕过。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class DenyApiKeyAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (IsApiKey(context.HttpContext.User))
        {
            throw new ForbiddenException("API 密钥不能执行该操作，请使用登录令牌。", "auth.api_key_not_allowed");
        }
    }

    public static bool IsApiKey(ClaimsPrincipal? user)
        => user?.HasClaim(TokenAuthenticationHandler.AuthMethodClaim, TokenAuthenticationHandler.ApiKeyMethod) == true;
}
