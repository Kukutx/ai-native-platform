using AiNative.Domain.Identity;

namespace AiNative.Contracts.Platform;

public sealed record FeatureDto(string Key, string Name, string ModuleKey);

public sealed record ModuleDto(
    string Key,
    string Name,
    string Description,
    bool IsCore,
    bool Enabled,
    IReadOnlyList<FeatureDto> Features);

public sealed record SetModuleEnabledRequest
{
    public required bool Enabled { get; init; }
}

public sealed record RoleDto(
    Guid Id,
    string Key,
    string Name,
    string? Description,
    IReadOnlyList<string> Features,
    bool IsSystem);

public sealed record SaveRoleRequest
{
    public required string Key { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required IReadOnlyList<string> Features { get; init; }
}

public sealed record MemberDto(
    Guid MembershipId,
    Guid UserId,
    string Email,
    string DisplayName,
    bool IsOwner,
    MembershipStatus Status,
    Guid? OrganizationId,
    IReadOnlyList<Guid> RoleIds,
    DateTimeOffset JoinedAt);

public sealed record AddMemberRequest
{
    public required string Email { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>
    /// 新用户的初始密码；邮箱已注册时忽略。
    /// </summary>
    public string? Password { get; init; }

    public Guid? OrganizationId { get; init; }

    public required IReadOnlyList<Guid> RoleIds { get; init; }
}

public sealed record UpdateMemberRequest
{
    public required IReadOnlyList<Guid> RoleIds { get; init; }

    public Guid? OrganizationId { get; init; }

    public required MembershipStatus Status { get; init; }
}

public sealed record OrganizationDto(Guid Id, string Name, Guid? ParentId, string Path, int Depth);

public sealed record CreateOrganizationRequest
{
    public required string Name { get; init; }

    public Guid? ParentId { get; init; }
}

public sealed record ModelProviderDto(string Name, string Kind, IReadOnlyList<string> Models, bool Configured);

public sealed record ModelCatalogDto(string DefaultProvider, string DefaultModel, IReadOnlyList<ModelProviderDto> Providers);

public sealed record AgentUsageDto(Guid AgentId, string Name, decimal CostUsd, decimal BudgetUsd);

public sealed record ModelUsageDto(string Provider, string Model, long InputTokens, long OutputTokens, decimal CostUsd);

public sealed record UsageSummaryDto(
    DateTimeOffset PeriodStart,
    decimal TotalCostUsd,
    IReadOnlyList<AgentUsageDto> ByAgent,
    IReadOnlyList<ModelUsageDto> ByModel);

public sealed record ApiKeyDto(
    Guid Id,
    string Name,
    string Prefix,
    IReadOnlyList<string> Features,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset CreatedAt);

public sealed record CreateApiKeyRequest
{
    public required string Name { get; init; }

    /// <summary>
    /// 为 true 时只授予查看类权限（*.view）。
    /// </summary>
    public bool ReadOnly { get; init; }

    /// <summary>
    /// 有效天数；为空表示长期有效（直到吊销）。
    /// </summary>
    public int? ExpiresInDays { get; init; }
}

/// <summary>
/// 创建结果。Token 只在此时返回一次，服务端只保存哈希。
/// </summary>
public sealed record CreatedApiKeyDto(ApiKeyDto Key, string Token);
