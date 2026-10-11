using System.Security.Claims;
using System.Text;
using AiNative.Application.Platform;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AiNative.Infrastructure.Security;

/// <summary>
/// 签发与校验平台访问令牌（HS256 JWT）。令牌只包含身份，权限每次请求实时解析。
/// </summary>
public sealed class TokenService(IOptions<AuthOptions> options, TimeProvider clock) : ITokenService
{
    private readonly AuthOptions _options = options.Value;
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    public IssuedToken Issue(Guid userId, Guid tenantId, Guid membershipId)
    {
        var now = clock.GetUtcNow();
        var expires = now.Add(_options.AccessTokenLifetime);
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Subject = new ClaimsIdentity(
            [
                new Claim(PlatformClaims.UserId, userId.ToString()),
                new Claim(PlatformClaims.TenantId, tenantId.ToString()),
                new Claim(PlatformClaims.MembershipId, membershipId.ToString()),
            ]),
            SigningCredentials = new SigningCredentials(SigningKey(), SecurityAlgorithms.HmacSha256),
        });
        return new IssuedToken(token, expires);
    }

    public async Task<ClaimsIdentity?> ValidateAsync(string token)
    {
        var result = await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = _options.Issuer,
            ValidAudience = _options.Audience,
            IssuerSigningKey = SigningKey(),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = PlatformClaims.UserId,
        }).ConfigureAwait(false);
        return result.IsValid ? result.ClaimsIdentity : null;
    }

    private SymmetricSecurityKey SigningKey() => new(Encoding.UTF8.GetBytes(_options.SigningKey));
}
