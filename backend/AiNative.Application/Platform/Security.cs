namespace AiNative.Application.Platform;

public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string hash, string password);
}

public sealed record IssuedToken(string AccessToken, DateTimeOffset ExpiresAt);

/// <summary>
/// 访问令牌只携带身份（用户、租户、成员身份）和令牌版本，权限每次请求从数据库解析，角色调整即时生效；
/// 令牌版本与用户当前版本不一致（修改过密码）的令牌一律拒绝。
/// </summary>
public interface ITokenService
{
    IssuedToken Issue(Guid userId, Guid tenantId, Guid membershipId, int tokenVersion);
}

public static class PlatformClaims
{
    public const string UserId = "sub";
    public const string TenantId = "tid";
    public const string MembershipId = "mid";

    /// <summary>
    /// 令牌版本。早于这个字段签发的令牌没有它，按 0 处理。
    /// </summary>
    public const string TokenVersion = "tv";

    public static int ReadTokenVersion(string? value) => int.TryParse(value, out var version) ? version : 0;
}
