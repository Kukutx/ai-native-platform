namespace AiNative.Contracts.Auth;

public sealed record RegisterRequest
{
    public required string Email { get; init; }

    public required string Password { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>
    /// 新建的租户（工作空间）名称。
    /// </summary>
    public required string TenantName { get; init; }

    /// <summary>
    /// 新空间的时区（IANA 名称），客户端一般传本机时区；不传或无法识别时用 UTC。
    /// </summary>
    public string? TimeZone { get; init; }
}

public sealed record LoginRequest
{
    public required string Email { get; init; }

    public required string Password { get; init; }

    /// <summary>
    /// 要进入的租户；为空时进入最近加入的一个。
    /// </summary>
    public Guid? TenantId { get; init; }
}

public sealed record SwitchTenantRequest
{
    public required Guid TenantId { get; init; }
}

public sealed record ChangePasswordRequest
{
    public required string CurrentPassword { get; init; }

    public required string NewPassword { get; init; }
}

public sealed record UserDto(Guid Id, string Email, string DisplayName);

/// <param name="TimeZone">空间时区（IANA 名称），"今天""本月"和按天统计都按它计算。</param>
public sealed record TenantSummaryDto(Guid Id, string Name, string Slug, bool IsOwner, string TimeZone);

/// <param name="DisabledFeatures">所属模块在当前空间未启用的功能权限。即使角色授予了，接口也会拒绝，客户端据此隐藏入口。</param>
public sealed record SessionDto(
    UserDto User,
    TenantSummaryDto Tenant,
    IReadOnlyList<TenantSummaryDto> Tenants,
    IReadOnlyList<string> Features,
    IReadOnlyList<string> EnabledModules,
    IReadOnlyList<string> DisabledFeatures);

public sealed record AuthResponse(string AccessToken, DateTimeOffset ExpiresAt, SessionDto Session);

/// <summary>
/// 登录页需要的站点设置，不需要登录即可读取。
/// </summary>
/// <param name="AllowRegistration">是否允许自助注册新空间。关闭后新成员只能由管理员加入已有空间。</param>
public sealed record AuthOptionsDto(bool AllowRegistration);
