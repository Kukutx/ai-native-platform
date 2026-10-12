using System.Text.Json;
using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Actions;
using AiNative.Application.Agents;
using AiNative.Application.Common;
using AiNative.Application.Modules;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Governance;
using AiNative.Domain.Agents;
using AiNative.Domain.Common;
using AiNative.Domain.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiNative.Application.Governance;

[RequireFeature(Features.Approvals.View)]
public sealed record ListApprovalsQuery(ApprovalStatus? Status = null) : IQuery<IReadOnlyList<ApprovalDto>>;

/// <summary>
/// 不走统一事务：先落定审批结论，再在独立作用域里执行动作，最后回写结果，避免一次失败让结论和执行结果互相牵连。
/// </summary>
[RequireFeature(Features.Approvals.Decide)]
public sealed record DecideApprovalCommand(Guid Id, DecideApprovalRequest Request) : IRequest<ApprovalDto>;

internal static class ApprovalMapping
{
    public static async Task<IReadOnlyList<ApprovalDto>> ToDtosAsync(
        IAppDbContext db,
        IActionInvoker actions,
        IReadOnlyList<Approval> approvals,
        CancellationToken cancellationToken)
    {
        var actors = await ActorNames.ResolveAsync(db, approvals.Select(a => a.RequestedBy), cancellationToken).ConfigureAwait(false);
        var titles = approvals.Count == 0
            ? new Dictionary<string, string>()
            : (await actions.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(a => a.Key, a => a.Title, StringComparer.Ordinal);
        return [.. approvals.Select(a => new ApprovalDto(
            a.Id,
            a.ActionKey,
            titles.GetValueOrDefault(a.ActionKey, a.ActionKey),
            PlatformJson.Parse(a.InputJson),
            a.Rationale,
            actors(a.RequestedBy),
            a.RunId,
            a.WorkItemId,
            a.WorkflowRunId,
            a.Status,
            a.DecidedByUserId,
            a.DecidedAt,
            a.DecisionNote,
            a.ResultJson is null ? null : PlatformJson.Parse(a.ResultJson),
            a.ExecutionError,
            a.CreatedAt))];
    }
}

/// <summary>
/// 审批到达最终状态（被拒绝，或批准后执行完成、执行失败、结果未知）时发布事件：恢复等待它的工作流运行，并触发订阅了审批结论的工作流。
/// </summary>
public static class ApprovalEvents
{
    public static Task PublishDecidedAsync(IServiceProvider services, Guid approvalId, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<IAppDbContext>();
        var actions = services.GetRequiredService<IActionInvoker>();
        var tenant = services.GetRequiredService<ITenantContext>();
        return services.GetRequiredService<IPlatformEvents>().PublishAsync(PlatformEventKeys.ApprovalDecided, approvalId, async () =>
        {
            var approval = await db.Approvals.AsNoTracking().FirstAsync(a => a.Id == approvalId, cancellationToken).ConfigureAwait(false);
            var dto = (await ApprovalMapping.ToDtosAsync(db, actions, [approval], cancellationToken).ConfigureAwait(false))[0];
            var actors = await ActorNames.ResolveAsync(db, [tenant.Actor], cancellationToken).ConfigureAwait(false);
            return new Dictionary<string, object?> { ["approval"] = dto, ["actor"] = actors(tenant.Actor) };
        }, cancellationToken);
    }
}

internal sealed class ApprovalHandlers(
    IAppDbContext db,
    IActionInvoker actions,
    ITenantContext tenant,
    ITenantScopeFactory scopes,
    IWakeupQueue wakeups,
    IChangeFeed changes,
    IServiceProvider services,
    TimeProvider clock) :
    IRequestHandler<ListApprovalsQuery, IReadOnlyList<ApprovalDto>>,
    IRequestHandler<DecideApprovalCommand, ApprovalDto>
{
    public async Task<IReadOnlyList<ApprovalDto>> HandleAsync(ListApprovalsQuery request, CancellationToken cancellationToken)
    {
        var query = db.Approvals.AsNoTracking();
        if (request.Status is { } status)
        {
            query = query.Where(a => a.Status == status);
        }

        var approvals = await query.OrderByDescending(a => a.CreatedAt).Take(200).ToListAsync(cancellationToken).ConfigureAwait(false);
        return await ApprovalMapping.ToDtosAsync(db, actions, approvals, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApprovalDto> HandleAsync(DecideApprovalCommand request, CancellationToken cancellationToken)
    {
        var approval = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == request.Id, cancellationToken).ConfigureAwait(false)
                       ?? throw new NotFoundException("approval", request.Id);
        var userId = tenant.Actor.IsUser ? tenant.Actor.Id!.Value : throw new ForbiddenException("只有成员可以处理审批。");
        if (approval.Status != ApprovalStatus.Pending)
        {
            throw new ConflictException("approval.already_decided", "该审批已经处理过。");
        }

        approval.Decide(request.Request.Approve, userId, request.Request.Note?.Trim(), clock.GetUtcNow());

        // 先以条件更新落定审批结论，并发的第二次处理会因状态已变而失败，保证动作最多执行一次。
        var affected = await db.Approvals
            .Where(a => a.Id == approval.Id && a.Status == ApprovalStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, approval.Status)
                .SetProperty(a => a.DecidedByUserId, approval.DecidedByUserId)
                .SetProperty(a => a.DecidedAt, approval.DecidedAt)
                .SetProperty(a => a.DecisionNote, approval.DecisionNote)
                .SetProperty(a => a.UpdatedAt, clock.GetUtcNow()), cancellationToken)
            .ConfigureAwait(false);
        if (affected == 0)
        {
            throw new ConflictException("approval.already_decided", "该审批已经处理过。");
        }

        // 结论已经落定，之后不再响应请求取消：客户端断开也要把动作执行完并写回结果。
        // 进程在这之间崩溃留下的"已批准、无结果"审批由心跳调度器标记为结果未知并通知申请人。
        cancellationToken = CancellationToken.None;
        Agent? agent = null;
        if (approval.RequestedBy is { Kind: ActorKind.Agent, Id: { } agentId })
        {
            agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken).ConfigureAwait(false);
        }

        if (approval.Status == ApprovalStatus.Approved)
        {
            await ExecuteAsync(approval, agent, userId, cancellationToken).ConfigureAwait(false);
            await db.Approvals.Where(a => a.Id == approval.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.ResultJson, approval.ResultJson)
                    .SetProperty(a => a.ExecutionError, approval.ExecutionError)
                    .SetProperty(a => a.UpdatedAt, clock.GetUtcNow()), cancellationToken)
                .ConfigureAwait(false);
        }

        // 工作流步骤发起的审批由工作流接着处理（下面的事件会恢复那次运行），不唤醒智能体。
        if (approval.WorkflowRunId is null && agent is not null && agent.Status != AgentStatus.Terminated)
        {
            var verdict = approval.Status == ApprovalStatus.Approved
                ? (approval.ExecutionError is null ? "已批准并执行成功" : $"已批准但执行失败：{approval.ExecutionError}")
                : "被拒绝";
            var note = string.IsNullOrWhiteSpace(approval.DecisionNote) ? string.Empty : $"（审批意见：{approval.DecisionNote}）";
            await wakeups.EnqueueAsync(
                agent.Id,
                new WakeupItem
                {
                    Source = WakeupSource.Approval,
                    Reason = $"你申请的 {approval.ActionKey} {verdict}{note}",
                    ApprovalId = approval.Id,
                    WorkItemId = approval.WorkItemId,
                    RequestedAt = clock.GetUtcNow(),
                },
                tenant.Actor,
                idempotencyKey: $"approval:{approval.Id}",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        changes.Publish(RealtimeEntities.Approval, approval.Id, RealtimeChangeKind.Updated);
        await ApprovalEvents.PublishDecidedAsync(services, approval.Id, cancellationToken).ConfigureAwait(false);
        var dtos = await ApprovalMapping.ToDtosAsync(db, actions, [approval], cancellationToken).ConfigureAwait(false);
        return dtos[0];
    }

    private async Task ExecuteAsync(Approval approval, Agent? agent, Guid approverId, CancellationToken cancellationToken)
    {
        // 以申请人的身份与权限、在独立作用域中执行；人工审批只解除"需要审批"这道闸，不放大权限。
        var actor = agent is null ? approval.RequestedBy : Actor.ForAgent(agent.Id);
        var features = agent?.Permissions ?? (IEnumerable<string>)tenant.Features;
        try
        {
            var result = await ActionToolset.InvokeAsync(
                scopes,
                tenant.TenantId,
                actor,
                features,
                approval.ActionKey,
                JsonSerializer.Deserialize<Dictionary<string, object?>>(approval.InputJson, PlatformJson.Options),
                new ActionInvocationOptions { RunId = approval.RunId, WorkItemId = approval.WorkItemId, WorkflowRunId = approval.WorkflowRunId, ApprovedBy = approverId },
                cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                // 没有输出的动作也写入 JSON null：ResultJson 与 ExecutionError 都为空只表示"执行结果没有写回"。
                approval.ResultJson = result.Output?.GetRawText() ?? "null";
            }
            else
            {
                approval.ExecutionError = result.Error ?? result.Status.ToString();
            }
        }
        catch (Exception ex)
        {
            approval.ExecutionError = $"执行出错：{ex.Message}";
        }
    }
}
