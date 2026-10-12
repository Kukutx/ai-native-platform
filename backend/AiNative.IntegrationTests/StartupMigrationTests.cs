using System.Net;
using AiNative.IntegrationTests.Infrastructure;
using Npgsql;

namespace AiNative.IntegrationTests;

/// <summary>
/// 启动时自动迁移：连接串指向的数据库还不存在时（全新的 PostgreSQL），应用要自己建库再迁移。
/// </summary>
public sealed class StartupMigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Creates_the_database_when_it_does_not_exist_yet()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = new NpgsqlConnectionStringBuilder(postgres.AdminConnectionString)
        {
            Database = $"it_missing_{Guid.NewGuid():N}"[..24],
        }.ConnectionString;

        try
        {
            await using var factory = new ApiFactory(connectionString);
            using var client = factory.CreateClient();

            var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), ct);

            ready.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await postgres.DropDatabaseAsync(connectionString);
        }
    }
}
