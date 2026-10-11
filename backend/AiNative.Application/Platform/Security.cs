namespace AiNative.Application.Platform;

public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string hash, string password);
}

public sealed record IssuedToken(string AccessToken, DateTimeOffset ExpiresAt);

/// <summary>
/// 访问令牌只携带身份（用户、租户、成员身份），权限每次请求从数据库解析，角色调整即时生效。
/// </summary>
public interface ITokenService
{
    IssuedToken Issue(Guid userId, Guid tenantId, Guid membershipId);
}

public static class PlatformClaims
{
    public const string UserId = "sub";
    public const string TenantId = "tid";
    public const string MembershipId = "mid";
}
