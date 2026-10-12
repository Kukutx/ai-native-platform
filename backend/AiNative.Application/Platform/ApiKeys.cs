using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Common;
using AiNative.Application.Modules;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Platform;
using AiNative.Domain.Identity;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AiNative.Application.Platform;

[RequireFeature(Features.Platform.ApiKeysUse)]
public sealed record ListApiKeysQuery : IQuery<IReadOnlyList<ApiKeyDto>>;

[RequireFeature(Features.Platform.ApiKeysUse)]
public sealed record CreateApiKeyCommand(CreateApiKeyRequest Request) : ICommand<CreatedApiKeyDto>;

[RequireFeature(Features.Platform.ApiKeysUse)]
public sealed record RevokeApiKeyCommand(Guid Id) : ICommand<ApiKeyDto>;

public sealed class CreateApiKeyCommandValidator : AbstractValidator<CreateApiKeyCommand>
{
    public CreateApiKeyCommandValidator()
    {
        RuleFor(x => x.Request.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Request.ExpiresInDays).InclusiveBetween(1, 3650).When(x => x.Request.ExpiresInDays is not null);
    }
}

/// <summary>
/// API 密钥格式：ank_{8 位前缀}_{43 位随机串}。前缀明文保存用于查找，整串只保存 SHA-256。
/// </summary>
public static class ApiKeyTokens
{
    public const string Scheme = "ank_";
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public static (string Token, string Prefix, string Hash) Generate()
    {
        var prefix = Scheme + RandomNumberGenerator.GetString(Alphabet, 8);
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = $"{prefix}_{secret}";
        return (token, prefix, Hash(token));
    }

    public static bool LooksLikeApiKey(string token) => token.StartsWith(Scheme, StringComparison.Ordinal);

    public static string? PrefixOf(string token)
    {
        if (!LooksLikeApiKey(token))
        {
            return null;
        }

        var separator = token.IndexOf('_', Scheme.Length);
        return separator == Scheme.Length + 8 ? token[..separator] : null;
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool Verify(string token, string expectedHash)
        => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(token)), Encoding.ASCII.GetBytes(expectedHash));
}

internal sealed class ApiKeyHandlers(
    IAppDbContext db,
    ITenantContext tenant,
    IModuleCatalog catalog,
    IMemoryCache cache,
    IPostCommitActions postCommit,
    IClusterBus cluster,
    TimeProvider clock) :
    IRequestHandler<ListApiKeysQuery, IReadOnlyList<ApiKeyDto>>,
    IRequestHandler<CreateApiKeyCommand, CreatedApiKeyDto>,
    IRequestHandler<RevokeApiKeyCommand, ApiKeyDto>
{
    public async Task<IReadOnlyList<ApiKeyDto>> HandleAsync(ListApiKeysQuery request, CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        var keys = await db.ApiKeys.AsNoTracking().Where(k => k.UserId == userId).OrderByDescending(k => k.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. keys.Select(ToDto)];
    }

    public Task<CreatedApiKeyDto> HandleAsync(CreateApiKeyCommand request, CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        var membershipId = tenant.MembershipId ?? throw new ForbiddenException("只有空间成员可以创建 API 密钥。");
        var (token, prefix, hash) = ApiKeyTokens.Generate();
        var key = new ApiKey
        {
            UserId = userId,
            MembershipId = membershipId,
            Name = request.Request.Name.Trim(),
            Prefix = prefix,
            SecretHash = hash,
            Features = request.Request.ReadOnly
                ? [.. catalog.AllFeatures.Select(f => f.Key).Where(k => k.EndsWith(".view", StringComparison.Ordinal))]
                : [],
            ExpiresAt = request.Request.ExpiresInDays is { } days ? clock.GetUtcNow().AddDays(days) : null,
        };
        db.ApiKeys.Add(key);
        return Task.FromResult(new CreatedApiKeyDto(ToDto(key), token));
    }

    public async Task<ApiKeyDto> HandleAsync(RevokeApiKeyCommand request, CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == request.Id && k.UserId == userId, cancellationToken).ConfigureAwait(false)
                  ?? throw new NotFoundException("api_key", request.Id);
        key.Revoke(clock.GetUtcNow());

        // 本实例立即失效，并广播给其他实例（广播丢失时最多再保留 ApiKeyAuthenticator.CacheDuration）。
        postCommit.Enqueue(_ =>
        {
            cache.Remove(ApiKeyAuthenticator.CacheKey(key.Prefix));
            cluster.Publish(ClusterTopics.ApiKeyCacheInvalidated, key.Prefix);
            return Task.CompletedTask;
        });
        return ToDto(key);
    }

    private Guid CurrentUserId() => tenant.Actor is { IsUser: true, Id: { } id } ? id : throw new ForbiddenException("只有成员可以管理 API 密钥。");

    private static ApiKeyDto ToDto(ApiKey key)
        => new(key.Id, key.Name, key.Prefix, key.Features, key.ExpiresAt, key.LastUsedAt, key.RevokedAt, key.CreatedAt);
}

public sealed record ApiKeyAccess(Guid KeyId, ResolvedAccess Access);

/// <summary>
/// 用 API 密钥换取身份：密钥有效、成员身份有效，权限取成员权限与密钥范围的交集。
/// </summary>
public interface IApiKeyAuthenticator
{
    Task<ApiKeyAccess?> AuthenticateAsync(string token, CancellationToken cancellationToken = default);
}

internal sealed class ApiKeyAuthenticator(
    IAppDbContext db,
    IAccessResolver access,
    IModuleCatalog catalog,
    IMemoryCache cache,
    TimeProvider clock) : IApiKeyAuthenticator
{
    internal static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(5);

    private sealed record CachedKey(Guid Id, Guid TenantId, Guid UserId, Guid MembershipId, string SecretHash, List<string> Features,
        DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset? LastUsedAt);

    public async Task<ApiKeyAccess?> AuthenticateAsync(string token, CancellationToken cancellationToken = default)
    {
        if (ApiKeyTokens.PrefixOf(token) is not { } prefix)
        {
            return null;
        }

        var cacheKey = CacheKey(prefix);
        if (!cache.TryGetValue(cacheKey, out CachedKey? key))
        {
            key = await db.ApiKeys.IgnoreQueryFilters().AsNoTracking().Where(k => k.Prefix == prefix)
                .Select(k => new CachedKey(k.Id, k.TenantId, k.UserId, k.MembershipId, k.SecretHash, k.Features, k.ExpiresAt, k.RevokedAt, k.LastUsedAt))
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            cache.Set(cacheKey, key, CacheDuration);
        }

        var now = clock.GetUtcNow();
        if (key is null || key.RevokedAt is not null || key.ExpiresAt <= now || !ApiKeyTokens.Verify(token, key.SecretHash))
        {
            return null;
        }

        var resolved = await access.ResolveAsync(key.UserId, key.TenantId, key.MembershipId, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return null;
        }

        if (key.LastUsedAt is null || now - key.LastUsedAt > LastUsedResolution)
        {
            await db.ApiKeys.IgnoreQueryFilters().Where(k => k.Id == key.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), cancellationToken).ConfigureAwait(false);
            cache.Set(cacheKey, key with { LastUsedAt = now }, CacheDuration);
        }

        return new ApiKeyAccess(key.Id, resolved with { Features = Restrict(resolved.Features, key.Features) });
    }

    internal static string CacheKey(string prefix) => $"api_key:{prefix}";

    /// <summary>
    /// 两组带通配符的授权取交集：在全部已知功能里逐个判断，结果是具体的功能键列表。
    /// </summary>
    private IReadOnlyList<string> Restrict(IReadOnlyList<string> granted, List<string> scopes)
        => scopes.Count == 0
            ? granted
            : [.. catalog.AllFeatures.Select(f => f.Key).Where(k => FeatureMatcher.IsGranted(granted, k) && FeatureMatcher.IsGranted(scopes, k))];
}

/// <summary>
/// 其他实例上吊销了 API 密钥后，清掉本实例对应的缓存。
/// </summary>
internal sealed class ApiKeyCacheInvalidationHandler(IMemoryCache cache) : IClusterMessageHandler
{
    public string Topic => ClusterTopics.ApiKeyCacheInvalidated;

    public Task HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (payload.GetString() is { } prefix)
        {
            cache.Remove(ApiKeyAuthenticator.CacheKey(prefix));
        }

        return Task.CompletedTask;
    }
}
