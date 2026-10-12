using AiNative.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Common;

/// <summary>
/// 编辑时的乐观并发检查。
/// </summary>
public static class OptimisticConcurrency
{
    /// <summary>
    /// 只有 UpdatedAt 仍等于客户端读到的值时才把它推进到 now。条件更新同时锁住这一行直到事务提交，
    /// 检查与随后的写入之间不会再被别人改掉。没有匹配的行时抛出带 <paramref name="code"/> 的冲突。
    /// </summary>
    public static async Task EnsureUnchangedAsync<T>(
        IQueryable<T> set,
        Guid id,
        DateTimeOffset expected,
        DateTimeOffset now,
        string code,
        string message,
        CancellationToken cancellationToken)
        where T : Entity
    {
        // PostgreSQL 时间戳精确到微秒，客户端回传的值按同样精度比较。
        var version = new DateTimeOffset(expected.UtcTicks - (expected.UtcTicks % 10), TimeSpan.Zero);
        var matched = await set.Where(e => e.Id == id && e.UpdatedAt == version)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        if (matched == 0)
        {
            throw new ConflictException(code, message);
        }
    }
}
