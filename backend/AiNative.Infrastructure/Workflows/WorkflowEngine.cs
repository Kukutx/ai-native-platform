using System.Collections.Concurrent;
using AiNative.Application.Abstractions;
using AiNative.Application.Tenancy;
using AiNative.Application.Workflows;
using AiNative.Domain.Common;
using AiNative.Domain.Workflows;
using AiNative.Infrastructure.Cluster;
using AiNative.Infrastructure.Common;
using AiNative.Infrastructure.Dispatching;
using AiNative.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiNative.Infrastructure.Workflows;

/// <summary>
/// 通知工作流引擎有新的运行。
/// </summary>
internal sealed class WorkflowSignal : CoalescingSignal, IWorkflowSignal;

/// <summary>
/// 工作流引擎：触发到期的定时工作流，领取可执行的运行（排队中的、延时或等待超时到期的）并在独立作用域里执行。
/// 可以多实例部署：领取用 FOR UPDATE SKIP LOCKED 并记下执行实例，定时触发靠条件更新只触发一次，
/// 实例失联或执行崩溃留下的运行由 <see cref="WorkflowRunRecovery"/> 放回队列。
/// </summary>
internal sealed class WorkflowEngine(
    IServiceScopeFactory scopes,
    ITenantScopeFactory tenantScopes,
    WorkflowSignal signal,
    ClusterNode node,
    WorkflowRunRecovery recovery,
    IClusterBus bus,
    IOptions<WorkflowOptions> options,
    IOptions<ClusterOptions> cluster,
    TimeProvider clock,
    ILogger<WorkflowEngine> logger) : BackgroundService
{
    private readonly WorkflowOptions _options = options.Value;
    private readonly TimeSpan _recoveryInterval = cluster.Value.HeartbeatInterval;
    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    private DateTimeOffset _nextScheduleSweep = DateTimeOffset.MinValue;
    private DateTimeOffset _nextRecovery = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Workflow engine disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 登记之前（或租约失效后）不领取：其他实例会把这期间领取的运行当成没人管的放回队列。
                if (node.IsRegistered)
                {
                    var now = clock.GetUtcNow();
                    if (now >= _nextRecovery)
                    {
                        _nextRecovery = now + _recoveryInterval;
                        await recovery.RecoverAsync([.. _running.Keys], stoppingToken).ConfigureAwait(false);
                    }

                    if (now >= _nextScheduleSweep)
                    {
                        _nextScheduleSweep = now + _options.ScheduleSweepInterval;
                        await FireSchedulesAsync(now, stoppingToken).ConfigureAwait(false);
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
                logger.LogError(ex, "Workflow engine iteration failed");
                await Task.Delay(TimeSpan.FromSeconds(2), clock, CancellationToken.None).ConfigureAwait(false);
            }
        }

        // 执行中的运行收到取消后保持 Running：放回队列，通知其他实例接着执行。
        await Task.WhenAll(_running.Values).ConfigureAwait(false);
        try
        {
            if (await recovery.ReleaseAsync(CancellationToken.None).ConfigureAwait(false) > 0)
            {
                bus.Publish(ClusterTopics.WorkflowQueued, new { });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to release workflow runs on shutdown");
        }
    }

    private async Task FireSchedulesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<(Guid Id, Guid TenantId, DateTimeOffset DueAt)> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.Workflows.IgnoreQueryFilters().AsNoTracking()
                .Where(w => w.IsEnabled && w.TriggerKind == WorkflowTriggerKind.Schedule && w.NextRunAt != null && w.NextRunAt <= now)
                .OrderBy(w => w.NextRunAt)
                .Select(w => new { w.Id, w.TenantId, w.NextRunAt })
                .Take(200)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            due = [.. rows.Select(r => (r.Id, r.TenantId, r.NextRunAt!.Value))];
        }

        var fired = false;
        foreach (var (workflowId, tenantId, dueAt) in due)
        {
            try
            {
                fired |= await tenantScopes.RunAsync(tenantId, Actor.System, [], sp => UnitOfWork.RunAsync(
                    sp,
                    () => sp.GetRequiredService<IWorkflowScheduleTrigger>().FireAsync(workflowId, dueAt, cancellationToken),
                    cancellationToken)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to fire scheduled workflow {WorkflowId}", workflowId);
            }
        }

        if (fired)
        {
            signal.Notify();
        }
    }

    private async Task ClaimAndDispatchAsync(CancellationToken cancellationToken)
    {
        var slots = _options.MaxConcurrentRuns - _running.Count;
        if (slots <= 0)
        {
            return;
        }

        List<(Guid Id, Guid TenantId)> claimed;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = clock.GetUtcNow();
            var ids = await db.Database.SqlQuery<Guid>($"""
                WITH picked AS (
                    SELECT id
                    FROM workflow_runs
                    WHERE status = 'Queued' OR (status = 'Waiting' AND resume_at <= {now})
                    ORDER BY created_at
                    LIMIT {slots}
                    FOR UPDATE SKIP LOCKED
                )
                UPDATE workflow_runs r
                SET status = 'Running', executor_id = {node.InstanceId}, started_at = COALESCE(r.started_at, {now}), updated_at = {now}
                FROM picked
                WHERE r.id = picked.id
                RETURNING r.id AS "Value"
                """).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (ids.Count == 0)
            {
                return;
            }

            var rows = await db.WorkflowRuns.IgnoreQueryFilters().AsNoTracking()
                .Where(r => ids.Contains(r.Id))
                .Select(r => new { r.Id, r.TenantId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            claimed = [.. rows.Select(r => (r.Id, r.TenantId))];
        }

        foreach (var (runId, tenantId) in claimed)
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
            // 租约失效时停下：其他实例可能已判定本实例失联，正要把这次运行放回队列。
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, leaseToken);
            await using (var scope = scopes.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<IWorkflowExecutor>().ExecuteAsync(tenantId, runId, cancellation.Token).ConfigureAwait(false);
            }

            // 被取消时运行停在 Running，之后放回队列；正常推进了才清零中断计数。
            if (!cancellation.IsCancellationRequested)
            {
                await recovery.ResetInterruptionsAsync(runId, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // 运行停在 Running，下一轮恢复时放回队列并累计中断次数。
            logger.LogError(ex, "Workflow run {RunId} crashed", runId);
        }
        finally
        {
            _running.TryRemove(runId, out _);
            signal.Notify();
        }
    }
}
