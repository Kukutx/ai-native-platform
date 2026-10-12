using AiNative.Application.Agents;
using AiNative.Application.Agents.Runtime;
using AiNative.Application.Governance;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Domain.Agents;
using AiNative.Domain.Common;
using AiNative.Domain.Governance;
using AiNative.Infrastructure.Cluster;
using AiNative.Infrastructure.Dispatching;
using AiNative.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiNative.Infrastructure.Agents;

/// <summary>
/// 接手没人管的智能体运行和审批：执行它们的实例已经停止或失联，或者本实例执行崩溃、没能收尾。
/// 每个实例的调度器定期调用；判断都写在条件更新里，多个实例同时执行也只处理一次。
/// </summary>
internal sealed class AgentRunRecovery(
    IServiceScopeFactory scopes,
    ITenantScopeFactory tenantScopes,
    ClusterNode node,
    IOptions<ClusterOptions> cluster,
    TimeProvider clock,
    ILogger<AgentRunRecovery> logger)
{
    internal const string InterruptedError = "执行所在的服务实例已停止或失联，执行被中断。";
    private const string ApprovalInterruptedError = "批准后执行过程中服务中断，结果未知。请先核实动作是否已经生效，需要时重新申请。";

    // 执行审批动作的实例仍在线时，等这么久还没写回结果才视为中断（审批动作通常几秒内完成）。
    private static readonly TimeSpan ApprovalExecutionGrace = TimeSpan.FromMinutes(10);

    private readonly TimeSpan _instanceTimeout = cluster.Value.InstanceTimeout;
    private bool _swept;

    /// <param name="localRuns">本实例正在执行的运行。调用方保证这期间不会领取新的运行。</param>
    public async Task RecoverAsync(IReadOnlyCollection<Guid> localRuns, CancellationToken cancellationToken)
    {
        await RecoverRunsAsync(localRuns, cancellationToken).ConfigureAwait(false);
        await RecoverApprovalsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RecoverRunsAsync(IReadOnlyCollection<Guid> localRuns, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // 同一时刻只需一个实例扫描；本实例自己的遗留运行下一轮再处理也不迟。
        if (!await db.TryLockForTransactionAsync("cluster-recovery:agent-runs", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var me = node.InstanceId;
        var local = localRuns.ToArray();
        var cutoff = await ClusterLiveness.CutoffAsync(db, _instanceTimeout, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();

        // 没人管的运行：执行实例已失联（没有记录执行实例的是升级前遗留的），或者记在本实例名下却不在本实例的执行表里。
        var interrupted = await db.Database.SqlQuery<Guid>($"""
            UPDATE heartbeat_runs r
            SET status = 'Failed', error = {InterruptedError}, finished_at = {now}, updated_at = {now}
            WHERE r.status IN ('Queued', 'Running')
              AND (r.executor_id IS NULL
                OR (r.executor_id = {me} AND NOT (r.id = ANY({local})))
                OR (r.executor_id <> {me} AND NOT EXISTS (
                    SELECT 1 FROM cluster_instances i WHERE i.id = r.executor_id AND i.last_seen_at > {cutoff})))
            RETURNING r.id AS "Value"
            """).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (interrupted.Count > 0)
        {
            await db.WorkItems.IgnoreQueryFilters()
                .Where(w => w.CheckoutRunId != null && interrupted.Contains(w.CheckoutRunId.Value))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(w => w.CheckoutRunId, (Guid?)null)
                        .SetProperty(w => w.ExecutionLockedAt, (DateTimeOffset?)null),
                    cancellationToken)
                .ConfigureAwait(false);
            logger.LogWarning("Marked {Count} interrupted agent runs as failed", interrupted.Count);
        }

        // 运行与智能体、唤醒的状态在同一事务里变化，只有执行中断才会不一致；首轮多扫一次，清理升级前的遗留。
        if (interrupted.Count > 0 || !_swept)
        {
            await db.Agents.IgnoreQueryFilters()
                .Where(a => a.Status == AgentStatus.Running
                    && !db.HeartbeatRuns.IgnoreQueryFilters().Any(r => r.AgentId == a.Id && (r.Status == RunStatus.Queued || r.Status == RunStatus.Running)))
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AgentStatus.Idle).SetProperty(a => a.UpdatedAt, now), cancellationToken)
                .ConfigureAwait(false);

            // 被中断的唤醒放回队列重新执行（同一智能体已有排队请求的则标记失败，原因不会丢：任务仍在待办里）。
            var orphaned = db.WakeupRequests.IgnoreQueryFilters()
                .Where(w => w.Status == WakeupStatus.Claimed
                    && !db.HeartbeatRuns.IgnoreQueryFilters().Any(r => r.Id == w.RunId && (r.Status == RunStatus.Queued || r.Status == RunStatus.Running)));
            await orphaned
                .Where(w => !db.WakeupRequests.IgnoreQueryFilters().Any(q => q.AgentId == w.AgentId && q.Status == WakeupStatus.Queued))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(w => w.Status, WakeupStatus.Queued)
                        .SetProperty(w => w.RunId, (Guid?)null)
                        .SetProperty(w => w.ClaimedAt, (DateTimeOffset?)null)
                        .SetProperty(w => w.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);
            await orphaned
                .ExecuteUpdateAsync(
                    s => s.SetProperty(w => w.Status, WakeupStatus.Failed)
                        .SetProperty(w => w.FinishedAt, now)
                        .SetProperty(w => w.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _swept = true;
    }

    /// <summary>
    /// 审批结论已落定但执行结果没有写回（执行的实例崩溃、写回失败）：结论不能重做，动作也不能盲目重跑（可能已经生效），
    /// 所以标记为结果未知，并唤醒申请的智能体去核实。执行实例已失联的立即处理，仍在线的等过宽限期。
    /// </summary>
    private async Task RecoverApprovalsAsync(CancellationToken cancellationToken)
    {
        List<(Guid Id, Guid TenantId)> stuck;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var dbNow = await ClusterLiveness.NowAsync(db, cancellationToken).ConfigureAwait(false);
            var cutoff = dbNow - _instanceTimeout;
            var decidedBefore = dbNow - ApprovalExecutionGrace;
            var me = node.InstanceId;
            var rows = await db.Approvals.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.Status == ApprovalStatus.Approved && a.ResultJson == null && a.ExecutionError == null)
                .Where(a => a.DecidedAt < decidedBefore
                    || a.ExecutorId == null
                    || (a.ExecutorId != me && !db.ClusterInstances.Any(i => i.Id == a.ExecutorId && i.LastSeenAt > cutoff)))
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
}
