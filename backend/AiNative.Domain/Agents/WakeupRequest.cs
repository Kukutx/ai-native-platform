using AiNative.Domain.Common;

namespace AiNative.Domain.Agents;

public enum WakeupSource
{
    OnDemand = 0,
    Assignment = 1,
    Comment = 2,
    Approval = 3,
    Timer = 4,
    Delegation = 5,
    Automation = 6,

    /// <summary>
    /// 前置任务都已关闭，等着它们的任务可以开始了。
    /// </summary>
    Dependency = 7,
}

public enum WakeupStatus
{
    Queued = 0,
    Claimed = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>
/// 一次唤醒的具体原因。同一智能体排队中的多个唤醒会合并成一个请求，原因逐条累积。
/// </summary>
public sealed record WakeupItem
{
    public WakeupSource Source { get; init; }

    public string Reason { get; init; } = string.Empty;

    public Guid? WorkItemId { get; init; }

    public Guid? ApprovalId { get; init; }

    public DateTimeOffset RequestedAt { get; init; }
}

/// <summary>
/// 智能体唤醒队列。每个智能体同一时刻最多一条 Queued 请求（数据库部分唯一索引保证），
/// 新的唤醒到来时合并进去并累加 CoalescedCount，避免短时间内重复跑多轮。
/// </summary>
public sealed class WakeupRequest : TenantEntity
{
    public Guid AgentId { get; set; }

    public WakeupStatus Status { get; set; } = WakeupStatus.Queued;

    public List<WakeupItem> Items { get; set; } = [];

    public int CoalescedCount { get; set; }

    public string? IdempotencyKey { get; set; }

    public Actor RequestedBy { get; set; }

    public Guid? RunId { get; set; }

    public DateTimeOffset? ClaimedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}
