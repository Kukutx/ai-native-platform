using AiNative.Domain.Common;

namespace AiNative.Domain.Identity;

/// <summary>
/// 平台用户（跨租户）。一个用户可以通过 Membership 加入多个租户。
/// </summary>
public sealed class User : Entity
{
    public required string Email { get; set; }

    public required string DisplayName { get; set; }

    public required string PasswordHash { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>
    /// 令牌版本：写进签发的登录令牌，修改密码时递增，之前签发的令牌随之全部失效。
    /// </summary>
    public int TokenVersion { get; set; }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
