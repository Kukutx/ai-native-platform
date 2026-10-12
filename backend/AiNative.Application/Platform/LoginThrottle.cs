using AiNative.Application.Common;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace AiNative.Application.Platform;

/// <summary>
/// 登录失败限制（Auth:LoginThrottle 配置节）：同一邮箱在时间窗口内连续输错达到上限后暂时拒绝登录，
/// 防止针对单个账号的密码猜测。按 IP 的请求频率限制在接口层另外处理。
/// </summary>
public sealed class LoginThrottleOptions
{
    public const string SectionName = "Auth:LoginThrottle";

    /// <summary>
    /// 窗口内允许的失败次数，0 表示不限制。
    /// </summary>
    public int MaxFailures { get; set; } = 10;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// 计数放在本实例内存里：多实例部署时每个实例各自计数，足以把猜测速度压到没有意义的程度。
/// </summary>
public sealed class LoginThrottle(IMemoryCache cache, IOptions<LoginThrottleOptions> options, TimeProvider clock)
{
    private readonly LoginThrottleOptions _options = options.Value;

    public void EnsureAllowed(string email)
    {
        if (_options.MaxFailures > 0 && cache.TryGetValue(Key(email), out Counter? counter) && counter is not null
            && counter.Failures >= _options.MaxFailures && counter.ExpiresAt > clock.GetUtcNow())
        {
            throw new RateLimitedException("auth.too_many_attempts", "密码输错次数过多，请稍后再试。");
        }
    }

    public void RecordFailure(string email)
    {
        if (_options.MaxFailures <= 0)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var key = Key(email);
        lock (cache)
        {
            var counter = cache.TryGetValue(key, out Counter? existing) && existing is not null && existing.ExpiresAt > now
                ? existing with { Failures = existing.Failures + 1 }
                : new Counter(1, now.Add(_options.Window));
            cache.Set(key, counter, counter.ExpiresAt);
        }
    }

    public void Reset(string email) => cache.Remove(Key(email));

    private static string Key(string email) => $"login-failures:{email}";

    private sealed record Counter(int Failures, DateTimeOffset ExpiresAt);
}
