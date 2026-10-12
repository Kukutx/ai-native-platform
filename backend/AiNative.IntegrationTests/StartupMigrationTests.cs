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

    [Fact]
    public async Task Instances_starting_together_create_and_migrate_the_database_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = new NpgsqlConnectionStringBuilder(postgres.AdminConnectionString)
        {
            Database = $"it_missing_{Guid.NewGuid():N}"[..24],
        }.ConnectionString;

        try
        {
            await using var first = new ApiFactory(connectionString);
            await using var second = new ApiFactory(connectionString);
            await Task.WhenAll(Task.Run(() => first.Server, ct), Task.Run(() => second.Server, ct));
            using var firstClient = first.CreateClient();
            using var secondClient = second.CreateClient();

            (await firstClient.GetAsync(new Uri("/health/ready", UriKind.Relative), ct)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await secondClient.GetAsync(new Uri("/health/ready", UriKind.Relative), ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await postgres.DropDatabaseAsync(connectionString);
        }
    }
}
