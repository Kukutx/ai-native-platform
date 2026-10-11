using AiNative.Application.Agents.Runtime;
using AiNative.Application.Realtime;
using AiNative.Application.Work;
using AiNative.Domain.Work;
using AiNative.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiNative.Infrastructure.Work;

/// <summary>
/// 任务领取：一条条件更新完成"只有负责人、未关闭、未被其他执行持有（或持有已过期）才能领取"，
/// 领取时把待办推进到进行中。
/// </summary>
internal sealed class WorkItemCheckout(AppDbContext db, IChangeFeed changes, IOptions<AgentRunOptions> options, TimeProvider clock) : IWorkItemCheckout
{
    public async Task<bool> TryCheckoutAsync(Guid workItemId, Guid agentId, Guid runId, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var staleBefore = now - options.Value.CheckoutLease;
        var updated = await db.WorkItems
            .Where(w => w.Id == workItemId
                && w.AssigneeAgentId == agentId
                && w.Status != WorkItemStatus.Done
                && w.Status != WorkItemStatus.Cancelled
                && (w.CheckoutRunId == null || w.CheckoutRunId == runId || w.ExecutionLockedAt < staleBefore))
            .ExecuteUpdateAsync(
                s => s.SetProperty(w => w.CheckoutRunId, runId)
                    .SetProperty(w => w.ExecutionLockedAt, now)
                    .SetProperty(
                        w => w.Status,
                        w => w.Status == WorkItemStatus.Backlog || w.Status == WorkItemStatus.Todo ? WorkItemStatus.InProgress : w.Status)
                    .SetProperty(w => w.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        if (updated > 0)
        {
            changes.Publish(RealtimeEntities.WorkItem, workItemId, RealtimeChangeKind.Updated);
        }

        return updated > 0;
    }

    public async Task ReleaseAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        await db.WorkItems
            .Where(w => w.CheckoutRunId == runId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(w => w.CheckoutRunId, (Guid?)null)
                    .SetProperty(w => w.ExecutionLockedAt, (DateTimeOffset?)null)
                    .SetProperty(w => w.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
