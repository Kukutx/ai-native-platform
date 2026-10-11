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

public sealed record UserDto(Guid Id, string Email, string DisplayName);

public sealed record TenantSummaryDto(Guid Id, string Name, string Slug, bool IsOwner);

public sealed record SessionDto(
    UserDto User,
    TenantSummaryDto Tenant,
    IReadOnlyList<TenantSummaryDto> Tenants,
    IReadOnlyList<string> Features,
    IReadOnlyList<string> EnabledModules);

public sealed record AuthResponse(string AccessToken, DateTimeOffset ExpiresAt, SessionDto Session);
