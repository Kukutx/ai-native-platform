using AiNative.Application.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AiNative.Infrastructure.Persistence;

/// <summary>
/// 供 dotnet ef 生成迁移使用；不连接真实数据库。
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        PersistenceSetup.Configure(options, "Host=localhost;Database=ainative;Username=postgres;Password=postgres");
        return new AppDbContext(options.Options, new TenantContext());
    }
}

internal static class PersistenceSetup
{
    public static void Configure(DbContextOptionsBuilder options, string connectionString)
        => options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention();
}
