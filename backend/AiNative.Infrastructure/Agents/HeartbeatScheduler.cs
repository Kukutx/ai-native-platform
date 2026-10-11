using System.Collections.Concurrent;
using AiNative.Application.Agents;
using AiNative.Application.Agents.Runtime;
using AiNative.Application.Governance;
using AiNative.Application.Tenancy;
using AiNative.Domain.Agents;
using AiNative.Domain.Common;
using AiNative.Domain.Governance;
using AiNative.Application.Realtime;
using AiNative.Infrastructure.Dispatching;
using AiNative.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiNative.Infrastructure.Agents;

/// <summary>
/// 心跳调度器：领取排队中的唤醒、创建运行记录并在独立作用域里执行；同时负责定时心跳和重启后的恢复。
/// 领取用 FOR UPDATE SKIP LOCKED，多个调度循环不会抢到同一条；但重启恢复假定单实例部署，
/// 多实例需要再引入实例租约。
/// </summary>
internal sealed class HeartbeatScheduler(
    IServiceScopeFactory scopes,
    ITenantScopeFactory tenantScopes,
    WakeupSignal signal,
    IOptions<SchedulerOptions> options,
    TimeProvider clock,
    ILogger<HeartbeatScheduler> logger) : BackgroundService
{
    private const string InterruptedError = "服务重启，执行被中断。";
    private const string ApprovalInterruptedError = "批准后执行过程中服务中断，结果未知。请先核实动作是否已经生效，需要时重新申请。";

    // 审批动作通常几秒内完成；超过这个时间仍没有写回结果，视为执行被中断。
    private static readonly TimeSpan ApprovalExecutionGrace = TimeSpan.FromMinutes(10);

    private readonly SchedulerOptions _options = options.Value;
    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    private DateTimeOffset _nextTimerSweep = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Heartbeat scheduler disabled.");
            return;
        }

        try
        {
            await RecoverInterruptedRunsAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to recover interrupted runs");
        }

        try
        {
            // 启动时（单实例假定下）所有没写回结果的已批准审批都已中断，不必等宽限期。
            await RecoverInterruptedApprovalsAsync(clock.GetUtcNow(), stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to recover interrupted approvals");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = clock.GetUtcNow();
                if (now >= _nextTimerSweep)
                {
                    _nextTimerSweep = now + _options.TimerSweepInterval;
                    await EnqueueTimerWakeupsAsync(now, stoppingToken).ConfigureAwait(false);
                    await RecoverInterruptedApprovalsAsync(now - ApprovalExecutionGrace, stoppingToken).ConfigureAwait(false);
                }

                await ClaimAndDispatchAsync(stoppingToken).ConfigureAwait(false);
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

    private async Task RecoverInterruptedRunsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var interrupted = await db.HeartbeatRuns.IgnoreQueryFilters()
            .Where(r => r.Status == RunStatus.Queued || r.Status == RunStatus.Running)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (interrupted.Count > 0)
        {
            await db.HeartbeatRuns.IgnoreQueryFilters()
                .Where(r => interrupted.Contains(r.Id))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(r => r.Status, RunStatus.Failed)
                        .SetProperty(r => r.Error, InterruptedError)
                        .SetProperty(r => r.FinishedAt, now)
                        .SetProperty(r => r.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);
            await db.WorkItems.IgnoreQueryFilters()
                .Where(w => w.CheckoutRunId != null && interrupted.Contains(w.CheckoutRunId.Value))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(w => w.CheckoutRunId, (Guid?)null)
                        .SetProperty(w => w.ExecutionLockedAt, (DateTimeOffset?)null),
                    cancellationToken)
                .ConfigureAwait(false);
            logger.LogWarning("Marked {Count} interrupted runs as failed", interrupted.Count);
        }

        await db.Agents.IgnoreQueryFilters()
            .Where(a => a.Status == AgentStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AgentStatus.Idle).SetProperty(a => a.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);

        // 被中断的唤醒放回队列重新执行（同一智能体已有排队请求的则标记失败，原因不会丢：任务仍在待办里）。
        await db.WakeupRequests.IgnoreQueryFilters()
            .Where(w => w.Status == WakeupStatus.Claimed
                && !db.WakeupRequests.IgnoreQueryFilters().Any(q => q.AgentId == w.AgentId && q.Status == WakeupStatus.Queued))
            .ExecuteUpdateAsync(
                s => s.SetProperty(w => w.Status, WakeupStatus.Queued)
                    .SetProperty(w => w.RunId, (Guid?)null)
                    .SetProperty(w => w.ClaimedAt, (DateTimeOffset?)null)
                    .SetProperty(w => w.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
        await db.WakeupRequests.IgnoreQueryFilters()
            .Where(w => w.Status == WakeupStatus.Claimed)
            .ExecuteUpdateAsync(
                s => s.SetProperty(w => w.Status, WakeupStatus.Failed)
                    .SetProperty(w => w.FinishedAt, now)
                    .SetProperty(w => w.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 审批结论已落定但执行结果没有写回（进程崩溃、写回失败）：结论不能重做，动作也不能盲目重跑（可能已经生效），
    /// 所以标记为结果未知，并唤醒申请的智能体去核实。
    /// </summary>
    private async Task RecoverInterruptedApprovalsAsync(DateTimeOffset decidedBefore, CancellationToken cancellationToken)
    {
        List<(Guid Id, Guid TenantId)> stuck;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.Approvals.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.Status == ApprovalStatus.Approved && a.ResultJson == null && a.ExecutionError == null && a.DecidedAt < decidedBefore)
                .Select(a => new { a.Id, a.TenantId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            stuck = [.. rows.Select(r => (r.Id, r.TenantId))];
        }

        foreach (var (approvalId, tenantId) in stuck)
        {
            await tenantScopes.RunAsync(tenantId, Actor.System, [], sp => UnitOfWork.RunAsync(sp, async () =>
            {
                var db = sp.GetRequiredService<AppDbContext>();
                var now = clock.GetUtcNow();
                var marked = await db.Approvals
                    .Where(a => a.Id == approvalId && a.ResultJson == null && a.ExecutionError == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.ExecutionError, ApprovalInterruptedError).SetProperty(a => a.UpdatedAt, now), cancellationToken)
                    .ConfigureAwait(false);
                if (marked == 0)
                {
                    return false;
                }

                var approval = await db.Approvals.AsNoTracking().FirstAsync(a => a.Id == approvalId, cancellationToken).ConfigureAwait(false);

                // 工作流发起的审批：恢复那次运行（它会把这一步记为失败），不唤醒智能体。
                await ApprovalEvents.PublishDecidedAsync(sp, approval.Id, cancellationToken).ConfigureAwait(false);
                if (approval.WorkflowRunId is null
                    && approval.RequestedBy is { Kind: ActorKind.Agent, Id: { } agentId }
                    && await db.Agents.AnyAsync(a => a.Id == agentId && a.Status != AgentStatus.Terminated, cancellationToken).ConfigureAwait(false))
                {
                    await sp.GetRequiredService<IWakeupQueue>().EnqueueAsync(
                        agentId,
                        new WakeupItem
                        {
                            Source = WakeupSource.Approval,
                            Reason = $"你申请的 {approval.ActionKey} 已批准，但执行过程中服务中断，结果未知。请核实是否已经生效，需要时重新申请。",
                            ApprovalId = approval.Id,
                            WorkItemId = approval.WorkItemId,
                            RequestedAt = now,
                        },
                        Actor.System,
                        $"approval:{approval.Id}:interrupted",
                        cancellationToken).ConfigureAwait(false);
                }

                sp.GetRequiredService<IChangeFeed>().Publish(RealtimeEntities.Approval, approval.Id, RealtimeChangeKind.Updated);
                logger.LogWarning("Approval {ApprovalId} was approved but its execution result was never recorded", approval.Id);
                return true;
            }, cancellationToken)).ConfigureAwait(false);
        }
    }

    private async Task EnqueueTimerWakeupsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
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
        var work = new Task<Task>(() => ExecuteRunAsync(tenantId, runId, stoppingToken));
        _running[runId] = work.Unwrap();
        work.Start(TaskScheduler.Default);
    }

    private async Task ExecuteRunAsync(Guid tenantId, Guid runId, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IAgentRuntime>().ExecuteAsync(tenantId, runId, stoppingToken).ConfigureAwait(false);
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
