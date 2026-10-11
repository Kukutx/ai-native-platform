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

    /// <summary>
    /// 所属项目与里程碑。里程碑一定属于同一个项目（由应用层校验）。
    /// </summary>
    public Guid? ProjectId { get; private set; }

    public Guid? MilestoneId { get; private set; }

    public DateOnly? DueDate { get; set; }

    public Guid? AssigneeAgentId { get; set; }

    public Guid? AssigneeUserId { get; set; }

    public Guid? CheckoutRunId { get; set; }

    public DateTimeOffset? ExecutionLockedAt { get; set; }

    public Actor CreatedBy { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public bool IsClosed => Status is WorkItemStatus.Done or WorkItemStatus.Cancelled;

    public void PlaceIn(Guid? projectId, Guid? milestoneId)
    {
        if (milestoneId is not null && projectId is null)
        {
            throw new DomainException("work_item.milestone_without_project", "里程碑必须属于某个项目。");
        }

        ProjectId = projectId;
        MilestoneId = milestoneId;
    }

    public void ChangeStatus(WorkItemStatus status, DateTimeOffset now)
    {
        // 状态没变时保留原来的完成时间：编辑已完成任务的其他字段不应改写它。
        if (status == Status && (CompletedAt is not null) == IsClosed)
        {
            return;
        }

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
