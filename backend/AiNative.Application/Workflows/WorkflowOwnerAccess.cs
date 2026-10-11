using AiNative.Application.Abstractions;
using AiNative.Application.Platform;
using AiNative.Domain.Agents;
using AiNative.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Workflows;

/// <summary>
/// 工作流所有者当前的身份与权限。
/// </summary>
internal sealed record WorkflowPrincipal(Actor Actor, IReadOnlyList<string> Features, IReadOnlyCollection<string> ExtraApprovalRequired, Agent? Agent);

/// <summary>
/// 解析工作流所有者此刻的权限。执行步骤和接收事件都以它为准，所有者被停用或收回权限后立即生效。
/// </summary>
internal sealed class WorkflowOwnerAccess(IAppDbContext db, IAccessResolver access)
{
    /// <summary>
    /// 所有者已不能代表工作流行事（离开空间、被停用、智能体暂停或终止）时抛出 <see cref="WorkflowRunException"/>。
    /// </summary>
    public async Task<WorkflowPrincipal> ResolveAsync(Actor owner, Guid tenantId, CancellationToken cancellationToken)
    {
        switch (owner)
        {
            case { Kind: ActorKind.User, Id: { } userId }:
                var membershipId = await db.Memberships.AsNoTracking().Where(m => m.UserId == userId).Select(m => (Guid?)m.Id)
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                var resolved = membershipId is { } id ? await access.ResolveAsync(userId, tenantId, id, cancellationToken).ConfigureAwait(false) : null;
                return resolved is null
                    ? throw new WorkflowRunException("工作流的所有者已不在当前空间或已被停用。请其他成员检查并重新保存工作流后再运行。")
                    : new WorkflowPrincipal(owner, resolved.Features, [], null);

            case { Kind: ActorKind.Agent, Id: { } agentId }:
                var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken).ConfigureAwait(false)
                            ?? throw new WorkflowRunException("工作流所属的智能体已不存在。");
                return agent.CanWork
                    ? new WorkflowPrincipal(owner, agent.Permissions, agent.ApprovalRequiredActions, agent)
                    : throw new WorkflowRunException($"工作流所属的智能体「{agent.Name}」已暂停或终止。");

            default:
                throw new WorkflowRunException("工作流没有有效的所有者。");
        }
    }
}
