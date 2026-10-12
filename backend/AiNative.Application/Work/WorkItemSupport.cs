using AiNative.Application.Abstractions;
using AiNative.Application.Agents;
using AiNative.Application.Common;
using AiNative.Application.Projects;
using AiNative.Application.Realtime;
using AiNative.Contracts.Common;
using AiNative.Contracts.Work;
using AiNative.Domain.Agents;
using AiNative.Domain.Common;
using AiNative.Domain.Work;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Work;

internal static class WorkItemMapping
{
    public static string KeyOf(int number) => $"T-{number}";

    public static WorkItemDto ToDto(this WorkItem item, Func<Actor, ActorDto> actors) => new(
        item.Id,
        item.Number,
        KeyOf(item.Number),
        item.Title,
        item.Description,
        item.Status,
        item.Priority,
        item.ParentId,
        item.ProjectId,
        item.MilestoneId,
        item.StartDate,
        item.DueDate,
        item.AssigneeAgentId,
        item.AssigneeUserId,
        item.CheckoutRunId,
        item.ExecutionLockedAt,
        actors(item.CreatedBy),
        item.CreatedAt,
        item.UpdatedAt,
        item.CompletedAt);

    public static WorkItemLinkDto ToLink(this WorkItem item)
        => new(item.Id, KeyOf(item.Number), item.Title, item.Status, item.StartDate, item.DueDate, item.ProjectId);

    public static CommentDto ToDto(this WorkItemComment comment, Func<Actor, ActorDto> actors)
        => new(comment.Id, comment.WorkItemId, actors(comment.Author), comment.Body, comment.RunId, comment.CreatedAt);
}

/// <summary>
/// 工作项编号分配（按租户原子递增）。
/// </summary>
public interface IWorkItemNumbers
{
    Task<int> NextAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 任务依赖图上的递归查询，在数据库里一次算完。
/// </summary>
public interface IWorkItemGraph
{
    /// <summary>
    /// <paramref name="workItemId"/> 是否已经直接或间接依赖 <paramref name="dependsOnId"/>。
    /// </summary>
    Task<bool> DependsOnAsync(Guid workItemId, Guid dependsOnId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 直接或间接依赖 <paramref name="rootId"/> 的任务之间的全部依赖（包括指向根任务的），用于顺延后续任务。
    /// </summary>
    Task<IReadOnlyList<ScheduleEdge>> DownstreamAsync(Guid rootId, CancellationToken cancellationToken = default);
}

/// <summary>
/// 工作项的原子领取：同一时刻只有一轮执行能持有，过期的锁可被接管。
/// </summary>
public interface IWorkItemCheckout
{
    Task<bool> TryCheckoutAsync(Guid workItemId, Guid agentId, Guid runId, CancellationToken cancellationToken = default);

    Task ReleaseAsync(Guid runId, CancellationToken cancellationToken = default);
}

/// <summary>
/// 工作项变化引发的协作：指派即唤醒、评论通知负责人、子任务完成通知上级任务负责人、前置任务都关闭后唤醒后续任务的负责人，
/// 项目里的任务状态变化交给 <see cref="ProjectWorkflow"/> 通知项目负责人。
/// 自己触发的变化不唤醒自己，避免智能体自我循环（前置任务关闭除外，见 <see cref="WakeReadyDependentsAsync"/>）。
/// </summary>
internal sealed class WorkItemWorkflow(IAppDbContext db, IWakeupQueue wakeups, IChangeFeed changes, ProjectWorkflow projects, TimeProvider clock)
{
    public async Task OnAssignedAsync(WorkItem item, Guid? previousAgentId, Actor actor, CancellationToken cancellationToken)
    {
        if (item.AssigneeAgentId is not { } agentId || agentId == previousAgentId)
        {
            return;
        }

        // 负责人变了就校验（包括已关闭的任务、智能体自己转派），之后的条件只决定要不要唤醒。
        await EnsureAgentCanBeAssignedAsync(agentId, cancellationToken).ConfigureAwait(false);

        // 前置任务还没关闭时先不唤醒：它领取不到这个任务，等前置任务都关闭后再唤醒。
        if (IsSelf(actor, agentId) || item.IsClosed || await IsWaitingAsync(item.Id, null, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await WakeAsync(
            agentId,
            actor.IsAgent ? WakeupSource.Delegation : WakeupSource.Assignment,
            $"任务 {WorkItemMapping.KeyOf(item.Number)}《{item.Title}》被指派给你",
            item.Id,
            actor,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task OnCommentedAsync(WorkItem item, Actor actor, string body, CancellationToken cancellationToken)
    {
        changes.Publish(RealtimeEntities.WorkItem, item.Id, RealtimeChangeKind.Updated);
        if (item.AssigneeAgentId is not { } agentId || IsSelf(actor, agentId) || item.IsClosed)
        {
            return;
        }

        var preview = body.Length > 120 ? body[..120] + "…" : body;
        await WakeAsync(agentId, WakeupSource.Comment, $"任务 {WorkItemMapping.KeyOf(item.Number)} 有新评论：{preview}", item.Id, actor, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task OnStatusChangedAsync(WorkItem item, WorkItemStatus previous, Actor actor, CancellationToken cancellationToken)
    {
        if (previous == item.Status)
        {
            return;
        }

        await projects.OnTaskStatusChangedAsync(item, previous, actor, cancellationToken).ConfigureAwait(false);
        if (!item.IsClosed)
        {
            return;
        }

        if (previous is not (WorkItemStatus.Done or WorkItemStatus.Cancelled))
        {
            await WakeReadyDependentsAsync(item, actor, cancellationToken).ConfigureAwait(false);
        }

        if (item.ParentId is not { } parentId)
        {
            return;
        }

        var parent = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == parentId, cancellationToken).ConfigureAwait(false);
        if (parent?.AssigneeAgentId is not { } parentAgentId || IsSelf(actor, parentAgentId) || parent.IsClosed)
        {
            return;
        }

        var verb = item.Status == WorkItemStatus.Done ? "已完成" : "已取消";
        await WakeAsync(
            parentAgentId,
            WakeupSource.Delegation,
            $"子任务 {WorkItemMapping.KeyOf(item.Number)}《{item.Title}》{verb}",
            parent.Id,
            actor,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 去掉一条依赖后，后续任务如果不再等任何前置任务，就唤醒它的负责智能体。
    /// </summary>
    /// <param name="predecessor">被去掉的前置任务；它本来就已关闭时什么都不变。</param>
    public async Task OnDependencyRemovedAsync(WorkItem successor, WorkItem predecessor, Actor actor, CancellationToken cancellationToken)
    {
        if (predecessor.IsClosed || successor.IsClosed || successor.AssigneeAgentId is not { } agentId || IsSelf(actor, agentId)
            || await IsWaitingAsync(successor.Id, null, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await WakeAsync(
            agentId,
            WakeupSource.Dependency,
            $"任务 {WorkItemMapping.KeyOf(successor.Number)}《{successor.Title}》不再等待前置任务 {WorkItemMapping.KeyOf(predecessor.Number)}，可以开始了",
            successor.Id,
            actor,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 任务是否还在等没关闭的前置任务，包括本次请求里刚加、尚未保存的依赖。
    /// </summary>
    /// <param name="except">不计入的前置任务（它的新状态还没保存）。</param>
    public async Task<bool> IsWaitingAsync(Guid workItemId, Guid? except, CancellationToken cancellationToken)
    {
        var added = db.WorkItemDependencies.Local
            .Where(d => d.WorkItemId == workItemId && db.Entry(d).State == EntityState.Added)
            .Select(d => d.DependsOnId)
            .ToList();
        return await db.WorkItems.AnyAsync(
            w => w.Id != except && w.Status != WorkItemStatus.Done && w.Status != WorkItemStatus.Cancelled
                 && (added.Contains(w.Id) || db.WorkItemDependencies.Any(d => d.WorkItemId == workItemId && d.DependsOnId == w.Id)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 任务关闭后，不再等任何前置任务的后续任务唤醒它们的负责智能体，聚焦到后续任务上。
    /// 自己做完前置任务也唤醒自己：本轮聚焦的是前置任务，关闭只发生一次，不会形成循环。
    /// </summary>
    /// <param name="item">刚关闭、新状态尚未保存的任务。</param>
    private async Task WakeReadyDependentsAsync(WorkItem item, Actor actor, CancellationToken cancellationToken)
    {
        var ready = await db.WorkItems.AsNoTracking()
            .Where(w => w.AssigneeAgentId != null && w.Status != WorkItemStatus.Done && w.Status != WorkItemStatus.Cancelled
                        && db.WorkItemDependencies.Any(d => d.WorkItemId == w.Id && d.DependsOnId == item.Id)
                        && !db.WorkItemDependencies.Any(d => d.WorkItemId == w.Id && d.DependsOnId != item.Id
                            && db.WorkItems.Any(p => p.Id == d.DependsOnId && p.Status != WorkItemStatus.Done && p.Status != WorkItemStatus.Cancelled)))
            .OrderBy(w => w.Number)
            .Select(w => new { w.Id, w.Number, w.Title, AgentId = w.AssigneeAgentId!.Value })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var verb = item.Status == WorkItemStatus.Done ? "已完成" : "已取消";
        foreach (var task in ready)
        {
            await WakeAsync(
                task.AgentId,
                WakeupSource.Dependency,
                $"前置任务 {WorkItemMapping.KeyOf(item.Number)}《{item.Title}》{verb}，任务 {WorkItemMapping.KeyOf(task.Number)}《{task.Title}》可以开始了",
                task.Id,
                actor,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureAgentCanBeAssignedAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var status = await db.Agents.Where(a => a.Id == agentId).Select(a => (AgentStatus?)a.Status)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (status is null)
        {
            throw new NotFoundException("agent", agentId);
        }

        if (status == AgentStatus.Terminated)
        {
            throw new ConflictException("agent.terminated", "不能把任务指派给已终止的智能体。");
        }
    }

    private Task WakeAsync(Guid agentId, WakeupSource source, string reason, Guid workItemId, Actor actor, CancellationToken cancellationToken)
        => wakeups.EnqueueAsync(
            agentId,
            new WakeupItem { Source = source, Reason = reason, WorkItemId = workItemId, RequestedAt = clock.GetUtcNow() },
            actor,
            cancellationToken: cancellationToken);

    private static bool IsSelf(Actor actor, Guid agentId) => actor.IsAgent && actor.Id == agentId;
}
