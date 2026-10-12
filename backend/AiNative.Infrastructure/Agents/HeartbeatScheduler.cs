using System.Collections.Concurrent;
using AiNative.Application.Abstractions;
using AiNative.Application.Agents;
using AiNative.Application.Agents.Runtime;
using AiNative.Application.Tenancy;
using AiNative.Domain.Agents;
using AiNative.Domain.Common;
using AiNative.Infrastructure.Cluster;
using AiNative.Infrastructure.Dispatching;
using AiNative.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiNative.Infrastructure.Agents;

/// <summary>
/// 心跳调度器：领取排队中的唤醒、创建运行记录并在独立作用域里执行；同时负责定时心跳和接手没人管的运行。
/// 可以多实例部署：领取用 FOR UPDATE SKIP LOCKED，多个实例不会抢到同一条；运行记下执行实例，
/// 实例失联后由存活的实例收尾（见 <see cref="AgentRunRecovery"/>）。
/// </summary>
internal sealed class HeartbeatScheduler(
    IServiceScopeFactory scopes,
    ITenantScopeFactory tenantScopes,
    WakeupSignal signal,
    ClusterNode node,
    AgentRunRecovery recovery,
    IDistributedLock locks,
    IOptions<SchedulerOptions> options,
    IOptions<ClusterOptions> cluster,
    TimeProvider clock,
    ILogger<HeartbeatScheduler> logger) : BackgroundService
{
    private readonly SchedulerOptions _options = options.Value;
    private readonly TimeSpan _recoveryInterval = cluster.Value.HeartbeatInterval;
    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    private DateTimeOffset _nextTimerSweep = DateTimeOffset.MinValue;
    private DateTimeOffset _nextRecovery = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Heartbeat scheduler disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 登记之前（或租约失效后）不领取：其他实例会把这期间领取的运行当成没人管的收回。
                if (node.IsRegistered)
                {
                    var now = clock.GetUtcNow();
                    if (now >= _nextRecovery)
                    {
                        _nextRecovery = now + _recoveryInterval;
                        await recovery.RecoverAsync([.. _running.Keys], stoppingToken).ConfigureAwait(false);
                    }

                    if (now >= _nextTimerSweep)
                    {
                        _nextTimerSweep = now + _options.TimerSweepInterval;
                        await EnqueueTimerWakeupsAsync(now, stoppingToken).ConfigureAwait(false);
                    }

                    await ClaimAndDispatchAsync(stoppingToken).ConfigureAwait(false);
                }

                await signal.WaitAsync(_options.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Heartbeat scheduler iteration failed");
                await Task.Delay(TimeSpan.FromSeconds(2), clock, CancellationToken.None).ConfigureAwait(false);
            }
        }

        // 执行中的运行收到取消后会自行以 Cancelled 收尾。
        await Task.WhenAll(_running.Values).ConfigureAwait(false);
    }

    private async Task EnqueueTimerWakeupsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // 多个实例同时扫描会给同一个智能体各入队一次（合并成一条唤醒里的两个原因），只让一个实例扫。
        await using var sweep = await locks.TryAcquireAsync("scheduler:timer-sweep", cancellationToken).ConfigureAwait(false);
        if (sweep is null)
        {
            return;
        }

        List<(Guid Id, Guid TenantId)> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var candidates = await db.Agents.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.HeartbeatIntervalSeconds > 0 && a.Status == AgentStatus.Idle)
                .Where(a => !db.WakeupRequests.IgnoreQueryFilters()
                    .Any(w => w.AgentId == a.Id && (w.Status == WakeupStatus.Queued || w.Status == WakeupStatus.Claimed)))
                .Select(a => new { a.Id, a.TenantId, a.HeartbeatIntervalSeconds, Last = a.LastHeartbeatAt ?? a.CreatedAt })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            due = [.. candidates.Where(a => a.Last.AddSeconds(a.HeartbeatIntervalSeconds) <= now).Select(a => (a.Id, a.TenantId))];
        }

        foreach (var (agentId, tenantId) in due)
        {
            await tenantScopes.RunAsync(tenantId, Actor.System, [], sp => UnitOfWork.RunAsync(sp, async () =>
            {
                await sp.GetRequiredService<IWakeupQueue>()
                    .EnqueueAsync(agentId, new WakeupItem { Source = WakeupSource.Timer, Reason = "定时心跳", RequestedAt = now }, Actor.System, null, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }, cancellationToken)).ConfigureAwait(false);
        }
    }

    private async Task ClaimAndDispatchAsync(CancellationToken cancellationToken)
    {
        var slots = _options.MaxConcurrentRuns - _running.Count;
        if (slots <= 0)
        {
            return;
        }

        var started = new List<(Guid TenantId, Guid RunId)>();
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = clock.GetUtcNow();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            // 只领取空闲（或上次出错）且没有进行中运行的智能体；暂停、终止的请求留在队列里。
            var claimed = await db.Database.SqlQuery<Guid>($"""
                WITH picked AS (
                    SELECT q.id
                    FROM wakeup_requests q
                    JOIN agents a ON a.id = q.agent_id
                    WHERE q.status = 'Queued'
                      AND a.status IN ('Idle', 'Error')
                      AND NOT EXISTS (
                          SELECT 1 FROM heartbeat_runs r
                          WHERE r.agent_id = q.agent_id AND r.status IN ('Queued', 'Running'))
                    ORDER BY q.created_at
                    LIMIT {slots}
                    FOR UPDATE OF q SKIP LOCKED
                )
                UPDATE wakeup_requests w
                SET status = 'Claimed', claimed_at = {now}, updated_at = {now}
                FROM picked
                WHERE w.id = picked.id
                RETURNING w.id AS "Value"
                """).ToListAsync(cancellationToken).ConfigureAwait(false);

            if (claimed.Count == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var wakeups = await db.WakeupRequests.IgnoreQueryFilters()
                .Where(w => claimed.Contains(w.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var wakeup in wakeups)
            {
                var run = new HeartbeatRun
                {
                    TenantId = wakeup.TenantId,
                    AgentId = wakeup.AgentId,
                    WakeupRequestId = wakeup.Id,
                    Status = RunStatus.Queued,
                    ExecutorId = node.InstanceId,
                };
                db.HeartbeatRuns.Add(run);
                wakeup.RunId = run.Id;
                started.Add((wakeup.TenantId, run.Id));
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var (tenantId, runId) in started)
        {
            Dispatch(tenantId, runId, cancellationToken);
        }
    }

    private void Dispatch(Guid tenantId, Guid runId, CancellationToken stoppingToken)
    {
        // 先登记再启动，避免运行瞬间结束时登记晚于移除。
        var work = new Task<Task>(() => ExecuteRunAsync(tenantId, runId, stoppingToken, node.LeaseToken));
        _running[runId] = work.Unwrap();
        work.Start(TaskScheduler.Default);
    }

    private async Task ExecuteRunAsync(Guid tenantId, Guid runId, CancellationToken stoppingToken, CancellationToken leaseToken)
    {
        try
        {
            // 租约失效时停下：其他实例可能已判定本实例失联，正要接手这次运行。
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, leaseToken);
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IAgentRuntime>().ExecuteAsync(tenantId, runId, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent run {RunId} crashed", runId);
        }
        finally
        {
            _running.TryRemove(runId, out _);
            signal.Notify();
        }
    }
}
