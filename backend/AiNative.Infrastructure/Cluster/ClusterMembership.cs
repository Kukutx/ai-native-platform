using AiNative.Application.Abstractions;
using AiNative.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiNative.Infrastructure.Cluster;

/// <summary>
/// 实例登记与租约：启动时登记，定期报到，停止时注销。
/// 连续半个超时没有报到成功（数据库不可达、进程长时间卡住）就收回租约，停下手里的运行；
/// 这样在其他实例按超时判定本实例失联、接手这些运行之前，本实例已经不再写入，不会重复执行。
/// </summary>
internal sealed class ClusterMembership(
    ClusterNode node,
    IClusterBus bus,
    IServiceScopeFactory scopes,
    IOptions<ClusterOptions> options,
    TimeProvider clock,
    ILogger<ClusterMembership> logger) : BackgroundService
{
    private readonly ClusterOptions _options = options.Value;
    private long _lastRenewal;
    private DateTimeOffset _nextCleanup = DateTimeOffset.MinValue;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // 调度器在本服务之后启动，先登记，它们一开始就能领取运行。
        try
        {
            await RegisterAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to register cluster instance {InstanceId}; will retry", node.InstanceId);
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // 调度器已先停止：注销后其他实例立即把本实例遗留的运行当成无主的接手，不必等超时。
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlAsync($"DELETE FROM cluster_instances WHERE id = {node.InstanceId}", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to unregister cluster instance {InstanceId}", node.InstanceId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    if (node.IsRegistered)
                    {
                        await RenewAsync(stoppingToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await RegisterAsync(stoppingToken).ConfigureAwait(false);
                    }

                    var now = clock.GetUtcNow();
                    if (now >= _nextCleanup)
                    {
                        _nextCleanup = now + _options.InstanceTimeout;
                        await CleanupAsync(stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Cluster heartbeat failed");
                }

                FenceIfExpired();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        // 计时从发出请求前开始：数据库记下的报到时间只会更晚，本地据此判断租约过期总是偏早，留足余量。
        var sentAt = clock.GetTimestamp();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO cluster_instances (id, host_name, started_at, last_seen_at)
            VALUES ({node.InstanceId}, {node.HostName}, now(), now())
            ON CONFLICT (id) DO UPDATE SET last_seen_at = now()
            """, cancellationToken).ConfigureAwait(false);
        await RefreshPeersAsync(db, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _lastRenewal, sentAt);
        node.MarkRegistered();

        // 告诉已有实例：从现在起有人在听，消息要广播出来。
        bus.Publish(ClusterTopics.InstanceJoined, new { node.InstanceId });
        logger.LogInformation("Cluster instance {InstanceId} registered on {HostName}", node.InstanceId, node.HostName);
    }

    private async Task RenewAsync(CancellationToken cancellationToken)
    {
        var sentAt = clock.GetTimestamp();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var renewed = await db.Database.ExecuteSqlAsync($"UPDATE cluster_instances SET last_seen_at = now() WHERE id = {node.InstanceId}", cancellationToken)
            .ConfigureAwait(false);
        if (renewed == 0)
        {
            // 登记已被清理：其他实例早已判定本实例失联并接手了它的运行。先停下手里的，再重新登记。
            logger.LogError("Cluster instance {InstanceId} lost its registration; cancelling local runs and re-registering", node.InstanceId);
            node.RevokeLease();
            await RegisterAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await RefreshPeersAsync(db, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _lastRenewal, sentAt);
    }

    private async Task RefreshPeersAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var cutoff = await ClusterLiveness.CutoffAsync(db, _options.InstanceTimeout, cancellationToken).ConfigureAwait(false);
        node.SetHasPeers(await db.ClusterInstances.AnyAsync(i => i.Id != node.InstanceId && i.LastSeenAt > cutoff, cancellationToken).ConfigureAwait(false));
    }

    private void FenceIfExpired()
    {
        if (node.IsRegistered && clock.GetElapsedTime(Volatile.Read(ref _lastRenewal)) > _options.InstanceTimeout / 2)
        {
            logger.LogError("Cluster instance {InstanceId} could not renew its lease in time; cancelling local runs", node.InstanceId);
            node.RevokeLease();
        }
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = await ClusterLiveness.NowAsync(db, cancellationToken).ConfigureAwait(false);

        // 失联很久的实例：它领取过的运行早已被接手，留着只会让列表变长。
        var staleBefore = now - _options.InstanceTimeout * 10;
        await db.ClusterInstances.Where(i => i.LastSeenAt < staleBefore).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        // 超长消息只在收到通知的一瞬间被读取。
        var messagesBefore = now - PostgresClusterBus.MessageRetention;
        await db.ClusterMessages.Where(m => m.CreatedAt < messagesBefore).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
