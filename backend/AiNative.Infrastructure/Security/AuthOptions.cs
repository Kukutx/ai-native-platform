using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AiNative.Infrastructure.Security;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public string Issuer { get; set; } = "ai-native-platform";

    public string Audience { get; set; } = "ai-native-platform";

    /// <summary>
    /// HMAC 签名密钥，至少 32 个字符。生产环境必须通过环境变量（Auth__SigningKey）或密钥管理注入。
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>
/// 启动时检查签名密钥：缺失、太短，或在开发环境以外沿用了仓库里公开的开发密钥，都拒绝启动。
/// 密钥泄露意味着任何人都能伪造任意成员的登录令牌，宁可起不来也不能带着它上线。
/// </summary>
internal sealed class AuthOptionsValidator(IHostEnvironment environment) : IValidateOptions<AuthOptions>
{
    /// <summary>
    /// 仓库里的开发密钥都以此开头（见 appsettings.Development.json）。
    /// </summary>
    public const string DevelopmentKeyPrefix = "dev-only-";

    public const int MinimumKeyLength = 32;

    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SigningKey))
        {
            return ValidateOptionsResult.Fail(
                "缺少令牌签名密钥：请设置 Auth:SigningKey（环境变量 Auth__SigningKey），用至少 32 个字符的随机字符串，例如 openssl rand -base64 48 的输出。");
        }

        if (options.SigningKey.Length < MinimumKeyLength)
        {
            return ValidateOptionsResult.Fail($"令牌签名密钥 Auth:SigningKey 太短：至少需要 {MinimumKeyLength} 个字符。");
        }

        if (!environment.IsDevelopment() && options.SigningKey.StartsWith(DevelopmentKeyPrefix, StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Fail(
                "令牌签名密钥 Auth:SigningKey 仍是仓库里公开的开发密钥，任何人都能用它伪造登录令牌。开发环境以外请换成自己生成的随机密钥。");
        }

        return ValidateOptionsResult.Success;
    }
}
