using AiNative.Application.Platform;
using Microsoft.AspNetCore.Identity;

namespace AiNative.Infrastructure.Security;

/// <summary>
/// 复用 ASP.NET Core Identity 的 PBKDF2 密码哈希（带版本标记，可平滑升级迭代次数）。
/// </summary>
internal sealed class PlatformPasswordHasher : IPasswordHasher
{
    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object Subject = new();

    public string Hash(string password) => Hasher.HashPassword(Subject, password);

    public bool Verify(string hash, string password)
        => Hasher.VerifyHashedPassword(Subject, hash, password) is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
}
