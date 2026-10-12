using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(AiNative.IntegrationTests.Infrastructure.PostgresFixture))]

namespace AiNative.IntegrationTests.Infrastructure;

/// <summary>
/// 整个测试程序集共用一个 PostgreSQL；每个测试建自己的数据库，互不干扰。
/// 设置 AINATIVE_TEST_POSTGRES（指向已有实例的连接串）可跳过容器启动。
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string AdminConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("AINATIVE_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(external))
        {
            AdminConnectionString = external;
            return;
        }

        _container = new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();
        await _container.StartAsync().ConfigureAwait(false);
        AdminConnectionString = _container.GetConnectionString();
    }

    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"it_{Guid.NewGuid():N}"[..20];
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        return new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = name }.ConnectionString;
    }

    /// <summary>
    /// 测试结束后关掉该库的连接池并删库。每个测试的连接串都不同，池里的空闲连接不会被复用，
    /// 不清理会一直占着服务器连接数直到空闲超时，并行跑全部测试时会撞上 max_connections。
    /// </summary>
    public async Task DropDatabaseAsync(string connectionString)
    {
        await using (var pooled = new NpgsqlConnection(connectionString))
        {
            NpgsqlConnection.ClearPool(pooled);
        }

        var name = new NpgsqlConnectionStringBuilder(connectionString).Database;
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }
}
