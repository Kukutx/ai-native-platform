using AiNative.Api.Security;
using AiNative.Application.Common;
using AiNative.Application.Platform;
using AiNative.Contracts.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiNative.Api.Controllers;

[Route("api/auth")]
public sealed class AuthController : ApiControllerBase
{
    /// <summary>
    /// 注册并创建自己的空间（附带一位默认的 AI 助理）。
    /// </summary>
    [AllowAnonymous]
    [HttpPost("register")]
    public Task<AuthResponse> Register(RegisterRequest request) => SendAsync(new RegisterCommand(request));

    /// <summary>
    /// 登录页需要的站点设置（例如是否开放注册）。
    /// </summary>
    [AllowAnonymous]
    [HttpGet("options")]
    public Task<AuthOptionsDto> Options() => SendAsync(new GetAuthOptionsQuery());

    [AllowAnonymous]
    [HttpPost("login")]
    public Task<AuthResponse> Login(LoginRequest request) => SendAsync(new LoginCommand(request));

    /// <summary>
    /// 当前会话：用户、所在空间、可切换的空间、权限与已启用模块。
    /// </summary>
    [HttpGet("session")]
    public Task<SessionDto> Session()
    {
        var principal = CurrentPrincipal();
        return SendAsync(new GetSessionQuery(principal.UserId, principal.TenantId, principal.MembershipId));
    }

    [HttpPost("switch-tenant")]
    public Task<AuthResponse> SwitchTenant(SwitchTenantRequest request)
        => SendAsync(new SwitchTenantCommand(CurrentPrincipal().UserId, request.TenantId));

    private PlatformPrincipal CurrentPrincipal()
        => PlatformPrincipal.From(User) ?? throw new UnauthorizedException();
}
