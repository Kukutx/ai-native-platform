using AiNative.Contracts.Common;
using AiNative.Domain.Work;

namespace AiNative.Contracts.Work;

public sealed record WorkItemDto(
    Guid Id,
    int Number,
    string Key,
    string Title,
    string Description,
    WorkItemStatus Status,
    WorkItemPriority Priority,
    Guid? ParentId,
    Guid? ProjectId,
    Guid? MilestoneId,
    DateOnly? DueDate,
    Guid? AssigneeAgentId,
    Guid? AssigneeUserId,
    Guid? CheckoutRunId,
    DateTimeOffset? ExecutionLockedAt,
    ActorDto CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed record WorkItemDetailDto(
    WorkItemDto Item,
    IReadOnlyList<WorkItemDto> Subtasks,
    IReadOnlyList<CommentDto> Comments);

public sealed record CreateWorkItemRequest
{
    public required string Title { get; init; }

    public string Description { get; init; } = string.Empty;

    public WorkItemStatus Status { get; init; } = WorkItemStatus.Todo;

    public WorkItemPriority Priority { get; init; } = WorkItemPriority.Medium;

    public Guid? ParentId { get; init; }

    /// <summary>
    /// 所属项目。省略且填了上级任务时沿用上级任务的项目和里程碑。
    /// </summary>
    public Guid? ProjectId { get; init; }

    /// <summary>
    /// 所属里程碑；只填它时项目取里程碑所在的项目。
    /// </summary>
    public Guid? MilestoneId { get; init; }

    public DateOnly? DueDate { get; init; }

    public Guid? AssigneeAgentId { get; init; }

    public Guid? AssigneeUserId { get; init; }
}

public sealed record UpdateWorkItemRequest
{
    public required string Title { get; init; }

    public string Description { get; init; } = string.Empty;

    public required WorkItemStatus Status { get; init; }

    public required WorkItemPriority Priority { get; init; }

    /// <summary>
    /// 所属项目与里程碑，与其他字段一样整体提交：留空表示不属于任何项目。
    /// </summary>
    public Guid? ProjectId { get; init; }

    public Guid? MilestoneId { get; init; }

    public DateOnly? DueDate { get; init; }

    public Guid? AssigneeAgentId { get; init; }

    public Guid? AssigneeUserId { get; init; }

    /// <summary>
    /// 编辑所基于的 <see cref="WorkItemDto.UpdatedAt"/>。提供时，任务在此之后被改过（其他成员或智能体）就返回 409，避免旧数据覆盖新改动。
    /// </summary>
    public DateTimeOffset? ExpectedUpdatedAt { get; init; }
}

public sealed record CommentDto(Guid Id, Guid WorkItemId, ActorDto Author, string Body, Guid? RunId, DateTimeOffset CreatedAt);

public sealed record AddCommentRequest
{
    public required string Body { get; init; }
}
