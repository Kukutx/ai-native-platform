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

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
