using AiNative.Application.Abstractions;
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
