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

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }
}
