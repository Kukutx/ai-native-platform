using System.Security.Claims;
using System.Text.Encodings.Web;
using AiNative.Application.Platform;
using AiNative.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AiNative.Api.Security;

/// <summary>
/// Bearer 令牌认证。令牌有效还不够：成员关系必须仍然有效（被移除或停用的成员立即失效），
/// 解析出的权限交给 <see cref="TenantContextMiddleware"/> 写入租户上下文。
/// WebSocket 无法设置请求头，实时通道允许用 access_token 查询参数传令牌。
/// </summary>
internal sealed class TokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TokenService tokens,
    IAccessResolver access) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Bearer";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ReadToken();
        if (token is null)
        {
            return AuthenticateResult.NoResult();
        }

        var identity = await tokens.ValidateAsync(token).ConfigureAwait(false);
        if (identity is null)
        {
            return AuthenticateResult.Fail("令牌无效或已过期。");
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(identity.Claims, SchemeName, PlatformClaims.UserId, null));
        if (PlatformPrincipal.From(principal) is not { } platform)
        {
            return AuthenticateResult.Fail("令牌缺少身份信息。");
        }

        var resolved = await access.ResolveAsync(platform.UserId, platform.TenantId, platform.MembershipId, Context.RequestAborted)
            .ConfigureAwait(false);
        if (resolved is null)
        {
            return AuthenticateResult.Fail("成员身份已失效。");
        }

        Context.Features.Set(resolved);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    private string? ReadToken()
    {
        var header = Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return header["Bearer ".Length..].Trim();
        }

        if (Request.Path.StartsWithSegments("/hubs") && Request.Query.TryGetValue("access_token", out var queryToken))
        {
            return queryToken.ToString();
        }

        return null;
    }
}
