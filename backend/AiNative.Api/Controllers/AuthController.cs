using AiNative.Api.Security;
using AiNative.Application.Common;
using AiNative.Application.Platform;
using AiNative.Contracts.Auth;
using AiNative.Api.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AiNative.Api.Controllers;

[Route("api/auth")]
public sealed class AuthController : ApiControllerBase
{
    /// <summary>
    /// 注册并创建自己的空间（附带一位默认的 AI 助理）。
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting(ApiSetup.AuthRateLimitPolicy)]
    [HttpPost("register")]
    public Task<AuthResponse> Register(RegisterRequest request) => SendAsync(new RegisterCommand(request));

    /// <summary>
    /// 登录页需要的站点设置（例如是否开放注册）。
    /// </summary>
    [AllowAnonymous]
    [HttpGet("options")]
    public Task<AuthOptionsDto> Options() => SendAsync(new GetAuthOptionsQuery());

    [AllowAnonymous]
    [EnableRateLimiting(ApiSetup.AuthRateLimitPolicy)]
    [HttpPost("login")]
    public Task<AuthResponse> Login(LoginRequest request) => SendAsync(new LoginCommand(request));

    /// <summary>
    /// 当前会话：用户、所在空间、可切换的空间、权限与已启用模块。
    /// </summary>
    [DenyApiKey]
    [HttpGet("session")]
    public Task<SessionDto> Session()
    {
        var principal = CurrentPrincipal();
        return SendAsync(new GetSessionQuery(principal.UserId, principal.TenantId, principal.MembershipId));
    }

    [DenyApiKey]
    [HttpPost("switch-tenant")]
    public Task<AuthResponse> SwitchTenant(SwitchTenantRequest request)
        => SendAsync(new SwitchTenantCommand(CurrentPrincipal().UserId, request.TenantId));

    /// <summary>
    /// 换发当前空间的新令牌（有效期重新计算）。客户端在令牌过半时调用，避免用到一半被要求重新登录。
    /// </summary>
    [DenyApiKey]
    [HttpPost("refresh")]
    public Task<AuthResponse> Refresh()
    {
        var principal = CurrentPrincipal();
        return SendAsync(new SwitchTenantCommand(principal.UserId, principal.TenantId));
    }

    /// <summary>
    /// 修改密码。之前签发的登录令牌全部失效（其他设备需要重新登录），返回本设备的新令牌。
    /// </summary>
    [DenyApiKey]
    [EnableRateLimiting(ApiSetup.AuthRateLimitPolicy)]
    [HttpPost("password")]
    public Task<AuthResponse> ChangePassword(ChangePasswordRequest request)
    {
        var principal = CurrentPrincipal();
        return SendAsync(new ChangePasswordCommand(principal.UserId, principal.TenantId, request));
    }

    private PlatformPrincipal CurrentPrincipal()
        => PlatformPrincipal.From(User) ?? throw new UnauthorizedException();
}
