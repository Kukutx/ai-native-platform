using AiNative.Domain.Common;

namespace AiNative.Domain.Work;

public enum WorkItemStatus
{
    Backlog = 0,
    Todo = 1,
    InProgress = 2,
    InReview = 3,
    Blocked = 4,
    Done = 5,
    Cancelled = 6,
}

public enum WorkItemPriority
{
    Low = 0,
    Medium = 1,
    High = 2,
    Urgent = 3,
}

/// <summary>
/// 工作项：人和智能体共用的任务单元，可拆分子任务、指派给智能体或成员。
/// 智能体开始处理前必须原子"领取"（CheckoutRunId + ExecutionLockedAt），防止多轮执行并发改同一项。
/// </summary>
public sealed class WorkItem : TenantEntity
{
    public int Number { get; set; }

    public required string Title { get; set; }

    public string Description { get; set; } = string.Empty;

    public WorkItemStatus Status { get; set; } = WorkItemStatus.Todo;

    public WorkItemPriority Priority { get; set; } = WorkItemPriority.Medium;

    public Guid? ParentId { get; set; }

    public Guid? AssigneeAgentId { get; set; }

    public Guid? AssigneeUserId { get; set; }

    public Guid? CheckoutRunId { get; set; }

    public DateTimeOffset? ExecutionLockedAt { get; set; }

    public Actor CreatedBy { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public bool IsClosed => Status is WorkItemStatus.Done or WorkItemStatus.Cancelled;

    public void ChangeStatus(WorkItemStatus status, DateTimeOffset now)
    {
        Status = status;
        CompletedAt = status is WorkItemStatus.Done or WorkItemStatus.Cancelled ? now : null;
    }

    public void AssignToAgent(Guid? agentId)
    {
        AssigneeAgentId = agentId;
        if (agentId is not null)
        {
            AssigneeUserId = null;
        }
    }

    public void AssignToUser(Guid? userId)
    {
        AssigneeUserId = userId;
        if (userId is not null)
        {
            AssigneeAgentId = null;
        }
    }
}
