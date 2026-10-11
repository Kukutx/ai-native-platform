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
        item.DueDate,
        item.AssigneeAgentId,
        item.AssigneeUserId,
        item.CheckoutRunId,
        item.ExecutionLockedAt,
        actors(item.CreatedBy),
        item.CreatedAt,
        item.UpdatedAt,
        item.CompletedAt);

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
/// 工作项的原子领取：同一时刻只有一轮执行能持有，过期的锁可被接管。
/// </summary>
public interface IWorkItemCheckout
{
    Task<bool> TryCheckoutAsync(Guid workItemId, Guid agentId, Guid runId, CancellationToken cancellationToken = default);

    Task ReleaseAsync(Guid runId, CancellationToken cancellationToken = default);
}

/// <summary>
/// 工作项变化引发的协作：指派即唤醒、评论通知负责人、子任务完成通知上级任务负责人，
/// 项目里的任务状态变化交给 <see cref="ProjectWorkflow"/> 通知项目负责人。
/// 自己触发的变化不唤醒自己，避免智能体自我循环。
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
        if (IsSelf(actor, agentId) || item.IsClosed)
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
        if (!item.IsClosed || item.ParentId is not { } parentId)
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
