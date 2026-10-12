using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Contracts.Agents;
using AiNative.Domain.Agents;
using AiNative.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Agents;

public static class AgentDefaults
{
    /// <summary>
    /// 新智能体的默认权限：了解团队、处理和讨论任务、唤醒同事、检索知识库、使用自己的长期记忆。
    /// </summary>
    public static readonly IReadOnlyList<string> Permissions =
    [
        Features.Agents.View,
        Features.Agents.Wake,
        Features.Tasks.View,
        Features.Tasks.Manage,
        Features.Tasks.Comment,
        Features.Knowledge.View,
        Features.Memory.Use,
        Projects.ProjectFeatures.View,
    ];
}

public static class BillingPeriod
{
    public static DateTimeOffset MonthStart(DateTimeOffset now) => new(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
}

internal static class AgentMapping
{
    public static AgentDto ToDto(this Agent agent, decimal spentThisMonth) => new(
        agent.Id,
        agent.Name,
        agent.Title,
        agent.Instructions,
        agent.ReportsToId,
        agent.Status,
        agent.PauseReason,
        agent.ModelProvider,
        agent.Model,
        agent.Permissions,
        agent.ApprovalRequiredActions,
        agent.HeartbeatIntervalSeconds,
        agent.LastHeartbeatAt,
        agent.MonthlyBudgetUsd,
        spentThisMonth,
        agent.LastError,
        agent.CreatedAt);

    public static WakeupDto ToDto(this WakeupRequest wakeup) => new(
        wakeup.Id,
        wakeup.AgentId,
        wakeup.Status,
        [.. wakeup.Items.Select(i => new WakeupItemDto(i.Source, i.Reason, i.WorkItemId, i.ApprovalId, i.RequestedAt))],
        wakeup.CoalescedCount,
        wakeup.RunId,
        wakeup.CreatedAt);

    public static RunDto ToDto(this HeartbeatRun run, string agentName) => new(
        run.Id,
        run.AgentId,
        agentName,
        run.WakeupRequestId,
        run.WorkItemId,
        run.Status,
        run.StartedAt,
        run.FinishedAt,
        run.Provider,
        run.Model,
        run.InputTokens,
        run.OutputTokens,
        run.CostUsd,
        run.Steps,
        run.Summary,
        run.Error,
        run.CreatedAt);
}

/// <summary>
/// 智能体本月花费，从费用流水聚合。
/// </summary>
public static class AgentSpend
{
    public static async Task<Dictionary<Guid, decimal>> ForMonthAsync(
        IAppDbContext db,
        DateTimeOffset now,
        IReadOnlyCollection<Guid>? agentIds,
        CancellationToken cancellationToken)
    {
        var start = BillingPeriod.MonthStart(now);
        var query = db.CostEvents.AsNoTracking()
            .Where(c => c.Actor.Kind == ActorKind.Agent && c.OccurredAt >= start);
        if (agentIds is not null)
        {
            query = query.Where(c => agentIds.Contains(c.Actor.Id!.Value));
        }

        var rows = await query
            .GroupBy(c => c.Actor.Id)
            .Select(g => new { AgentId = g.Key, Cost = g.Sum(c => c.CostUsd) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Where(r => r.AgentId.HasValue).ToDictionary(r => r.AgentId!.Value, r => r.Cost);
    }

    public static async Task<decimal> ForAgentAsync(IAppDbContext db, Guid agentId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var spend = await ForMonthAsync(db, now, [agentId], cancellationToken).ConfigureAwait(false);
        return spend.GetValueOrDefault(agentId);
    }
}

/// <summary>
/// 唤醒队列。实现保证同一智能体最多一条排队请求，新唤醒自动合并；必须在当前事务内执行。
/// </summary>
public interface IWakeupQueue
{
    Task<Guid> EnqueueAsync(
        Guid agentId,
        WakeupItem item,
        Actor requestedBy,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 通知调度器有新的唤醒（进程内信号，省去轮询等待）。
/// </summary>
public interface IWakeupSignal
{
    void Notify();
}
