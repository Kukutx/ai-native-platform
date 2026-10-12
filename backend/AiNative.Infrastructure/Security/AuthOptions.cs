using System.ComponentModel.DataAnnotations;

namespace AiNative.Infrastructure.Security;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public string Issuer { get; set; } = "ai-native-platform";

    public string Audience { get; set; } = "ai-native-platform";

    /// <summary>
    /// HMAC 签名密钥，至少 32 字节。生产环境必须通过环境变量或密钥管理注入。
    /// </summary>
    [Required]
    [MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromDays(7);
}
