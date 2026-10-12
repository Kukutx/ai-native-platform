using System.Security.Claims;
using System.Text.Encodings.Web;
using AiNative.Application.Platform;
using AiNative.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AiNative.Api.Security;

/// <summary>
/// Bearer 认证，接受两种凭据：登录得到的访问令牌（JWT），以及个人 API 密钥（ank_ 开头，供 MCP 客户端与外部集成）。
/// 凭据有效还不够：成员关系必须仍然有效（被移除或停用的成员立即失效），
/// 解析出的权限交给 <see cref="TenantContextMiddleware"/> 写入租户上下文。
/// WebSocket 无法设置请求头，实时通道允许用 access_token 查询参数传令牌；实时通道只接受登录令牌，不接受 API 密钥。
/// </summary>
internal sealed class TokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TokenService tokens,
    IAccessResolver access,
    IApiKeyAuthenticator apiKeys) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Bearer";

    /// <summary>
    /// 认证方式声明：值为 api_key 时表示本次请求用的是 API 密钥。
    /// </summary>
    public const string AuthMethodClaim = "amr";
    public const string ApiKeyMethod = "api_key";
    public const string ApiKeyIdClaim = "api_key_id";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ReadToken();
        if (token is null)
        {
            return AuthenticateResult.NoResult();
        }

        if (ApiKeyTokens.LooksLikeApiKey(token))
        {
            // 实时通道按成员身份重新解析权限，套不上密钥的权限范围与吊销检查，所以不对 API 密钥开放。
            return Request.Path.StartsWithSegments("/hubs")
                ? AuthenticateResult.Fail("实时通道不接受 API 密钥，请使用登录令牌。")
                : await AuthenticateApiKeyAsync(token).ConfigureAwait(false);
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

        if (PlatformClaims.ReadTokenVersion(principal.FindFirstValue(PlatformClaims.TokenVersion)) != resolved.TokenVersion)
        {
            return AuthenticateResult.Fail("登录已失效（密码已修改），请重新登录。");
        }

        Context.Features.Set(resolved);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    private async Task<AuthenticateResult> AuthenticateApiKeyAsync(string token)
    {
        var result = await apiKeys.AuthenticateAsync(token, Context.RequestAborted).ConfigureAwait(false);
        if (result is null)
        {
            return AuthenticateResult.Fail("API 密钥无效、已过期或已吊销。");
        }

        var resolved = result.Access;
        var identity = new ClaimsIdentity(
        [
            new Claim(PlatformClaims.UserId, resolved.UserId.ToString()),
            new Claim(PlatformClaims.TenantId, resolved.TenantId.ToString()),
            new Claim(PlatformClaims.MembershipId, resolved.MembershipId.ToString()),
            new Claim(AuthMethodClaim, ApiKeyMethod),
            new Claim(ApiKeyIdClaim, result.KeyId.ToString()),
        ], SchemeName, PlatformClaims.UserId, null);
        Context.Features.Set(resolved);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
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
