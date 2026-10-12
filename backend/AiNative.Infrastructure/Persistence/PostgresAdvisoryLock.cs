using AiNative.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AiNative.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL 会话级 advisory lock。持锁期间占用一条不进连接池的独立连接：关闭连接即释放，
/// 进程崩溃时数据库也会随会话结束自动释放，不会遗留死锁。
/// </summary>
internal sealed class PostgresAdvisoryLock(string connectionString) : IDistributedLock
{
    private readonly string _connectionString = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

    public async Task<IAsyncDisposable?> TryAcquireAsync(string key, CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended(@key, 0))", connection);
            command.Parameters.AddWithValue("key", key);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            {
                return connection;
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await connection.DisposeAsync().ConfigureAwait(false);
        return null;
    }
}

/// <summary>
/// PostgreSQL 事务级 advisory lock，跑在当前请求的数据库连接和事务上。
/// </summary>
internal sealed class PostgresTransactionLock(AppDbContext db) : ITransactionLock
{
    public async Task AcquireAsync(string key, CancellationToken cancellationToken = default)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("事务级锁只能在事务中使用。");
        }

        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken).ConfigureAwait(false);
    }
}

internal static class AdvisoryLocks
{
    /// <summary>
    /// 尝试在当前事务上加事务级 advisory lock，拿不到立即返回 false。用于多个实例都会定期执行、但同一时刻只需一个执行的维护任务；
    /// 跑在连接池的连接上，事务结束自动释放。
    /// </summary>
    public static async Task<bool> TryLockForTransactionAsync(this AppDbContext db, string key, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("事务级锁只能在事务中使用。");
        }

        return await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended({key}, 0)) AS \"Value\"")
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
