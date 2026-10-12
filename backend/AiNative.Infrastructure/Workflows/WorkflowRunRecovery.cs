using AiNative.Domain.Workflows;
using AiNative.Infrastructure.Cluster;
using AiNative.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiNative.Infrastructure.Workflows;

/// <summary>
/// 把没人管的工作流运行放回队列：执行它们的实例已经停止或失联，或者本实例执行崩溃（例如数据库瞬断）。
/// 执行进度与每一步的副作用在同一事务里提交，放回队列后从中断的步骤继续；连续中断多次的标记为失败，避免反复消耗。
/// </summary>
internal sealed class WorkflowRunRecovery(
    IServiceScopeFactory scopes,
    ClusterNode node,
    IOptions<ClusterOptions> cluster,
    TimeProvider clock,
    ILogger<WorkflowRunRecovery> logger)
{
    internal const int MaxInterruptions = 3;
    private const string ExhaustedError = "运行多次意外中断，已停止。请查看服务日志排查后重新运行。";

    private readonly TimeSpan _instanceTimeout = cluster.Value.InstanceTimeout;

    /// <param name="localRuns">本实例正在执行的运行。调用方保证这期间不会领取新的运行。</param>
    /// <returns>是否有运行放回了队列。</returns>
    public async Task<bool> RecoverAsync(IReadOnlyCollection<Guid> localRuns, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await db.TryLockForTransactionAsync("cluster-recovery:workflow-runs", cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var me = node.InstanceId;
        var local = localRuns.ToArray();
        var cutoff = await ClusterLiveness.CutoffAsync(db, _instanceTimeout, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();

        // 判断条件与智能体运行相同：执行实例已失联（或没有记录），或者记在本实例名下却不在执行表里。
        var exhausted = await db.Database.SqlQuery<Guid>($"""
            UPDATE workflow_runs r
            SET status = 'Failed', error = {ExhaustedError}, finished_at = {now}, updated_at = {now}, interruption_count = r.interruption_count + 1
            WHERE r.status = 'Running'
              AND r.interruption_count + 1 >= {MaxInterruptions}
              AND (r.executor_id IS NULL
                OR (r.executor_id = {me} AND NOT (r.id = ANY({local})))
                OR (r.executor_id <> {me} AND NOT EXISTS (
                    SELECT 1 FROM cluster_instances i WHERE i.id = r.executor_id AND i.last_seen_at > {cutoff})))
            RETURNING r.id AS "Value"
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        var requeued = await db.Database.ExecuteSqlAsync($"""
            UPDATE workflow_runs r
            SET status = 'Queued', updated_at = {now}, interruption_count = r.interruption_count + 1
            WHERE r.status = 'Running'
              AND (r.executor_id IS NULL
                OR (r.executor_id = {me} AND NOT (r.id = ANY({local})))
                OR (r.executor_id <> {me} AND NOT EXISTS (
                    SELECT 1 FROM cluster_instances i WHERE i.id = r.executor_id AND i.last_seen_at > {cutoff})))
            """, cancellationToken).ConfigureAwait(false);

        if (exhausted.Count > 0)
        {
            await db.WorkflowRunSteps.IgnoreQueryFilters()
                .Where(x => exhausted.Contains(x.RunId) && (x.Status == WorkflowStepStatus.Running || x.Status == WorkflowStepStatus.Waiting))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, WorkflowStepStatus.Failed)
                    .SetProperty(x => x.Error, ExhaustedError)
                    .SetProperty(x => x.FinishedAt, now)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken)
                .ConfigureAwait(false);
            logger.LogError("Marked {Count} workflow runs as failed after repeated interruptions", exhausted.Count);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (requeued > 0)
        {
            logger.LogWarning("Re-queued {Count} interrupted workflow runs", requeued);
        }

        return requeued > 0;
    }

    /// <summary>
    /// 正常推进了一次（完成或进入等待），之前的中断不再累计。
    /// </summary>
    public async Task ResetInterruptionsAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.WorkflowRuns.IgnoreQueryFilters()
            .Where(r => r.Id == runId && r.InterruptionCount > 0)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.InterruptionCount, 0), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 停机时本实例执行中的运行已收到取消、停在 Running：直接放回队列交给其他实例，不算意外中断。
    /// </summary>
    public async Task<int> ReleaseAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = clock.GetUtcNow();
        var me = node.InstanceId;
        return await db.WorkflowRuns.IgnoreQueryFilters()
            .Where(r => r.Status == WorkflowRunStatus.Running && r.ExecutorId == me)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, WorkflowRunStatus.Queued).SetProperty(r => r.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
    }
}
