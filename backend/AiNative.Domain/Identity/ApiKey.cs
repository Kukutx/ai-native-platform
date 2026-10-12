using AiNative.Domain.Common;

namespace AiNative.Domain.Identity;

/// <summary>
/// 个人 API 密钥：供 MCP 客户端、脚本和外部系统以某位成员的身份访问平台。
/// 只保存密钥的哈希；权限是成员当前权限与密钥范围的交集，成员被停用或移除后密钥随之失效。
/// </summary>
public sealed class ApiKey : TenantEntity
{
    public Guid UserId { get; set; }

    public Guid MembershipId { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// 明文前缀，用于查找和在界面上辨认（例如 ank_3fA9xQ2b）。
    /// </summary>
    public required string Prefix { get; set; }

    public required string SecretHash { get; set; }

    /// <summary>
    /// 密钥允许的功能范围；为空表示与成员权限一致。
    /// </summary>
    public List<string> Features { get; set; } = [];

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);

    public void Revoke(DateTimeOffset now)
    {
        if (RevokedAt is not null)
        {
            throw new DomainException("api_key.already_revoked", "该密钥已经吊销。");
        }

        RevokedAt = now;
    }
}
