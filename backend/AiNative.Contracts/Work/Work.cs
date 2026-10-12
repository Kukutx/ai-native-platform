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
    DateOnly? StartDate,
    DateOnly? DueDate,
    Guid? AssigneeAgentId,
    Guid? AssigneeUserId,
    Guid? CheckoutRunId,
    DateTimeOffset? ExecutionLockedAt,
    ActorDto CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

/// <param name="Dependencies">前置任务：它们都关闭（完成或取消）后这个任务才能开始。</param>
/// <param name="Dependents">后续任务：在等这个任务关闭。</param>
public sealed record WorkItemDetailDto(
    WorkItemDto Item,
    IReadOnlyList<WorkItemDto> Subtasks,
    IReadOnlyList<CommentDto> Comments,
    IReadOnlyList<WorkItemLinkDto> Dependencies,
    IReadOnlyList<WorkItemLinkDto> Dependents);

/// <summary>
/// 依赖关系另一端的任务摘要。
/// </summary>
public sealed record WorkItemLinkDto(
    Guid Id,
    string Key,
    string Title,
    WorkItemStatus Status,
    DateOnly? StartDate,
    DateOnly? DueDate,
    Guid? ProjectId);

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

    /// <summary>
    /// 计划开始日期；和截止日期都填时不能晚于截止日期。
    /// </summary>
    public DateOnly? StartDate { get; init; }

    public DateOnly? DueDate { get; init; }

    public Guid? AssigneeAgentId { get; init; }

    public Guid? AssigneeUserId { get; init; }

    /// <summary>
    /// 前置任务 ID：它们都关闭后这个任务才能开始。指派给智能体时，前置任务未关闭就先不唤醒它。
    /// </summary>
    public IReadOnlyList<Guid>? DependsOn { get; init; }
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

    public DateOnly? StartDate { get; init; }

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

public sealed record AddWorkItemDependencyRequest
{
    /// <summary>
    /// 前置任务 ID。不能是任务自己，也不能形成循环（前置任务已经直接或间接依赖这个任务）。
    /// </summary>
    public required Guid DependsOnId { get; init; }
}

public sealed record ScheduleWorkItemRequest
{
    /// <summary>
    /// 新的开始日期，留空表示清除。
    /// </summary>
    public DateOnly? StartDate { get; init; }

    /// <summary>
    /// 新的截止日期，留空表示清除。
    /// </summary>
    public DateOnly? DueDate { get; init; }

    /// <summary>
    /// 为 true 时顺延后续任务：开始日期早于本任务（以及被顺延任务）截止日期的未关闭后续任务，开始和截止日期一起往后挪，
    /// 沿依赖链传递。只往后顺延，不会自动提前。
    /// </summary>
    public bool ShiftDependents { get; init; }

    /// <summary>
    /// 编辑所基于的 <see cref="WorkItemDto.UpdatedAt"/>，任务在此之后被改过就返回 409。
    /// </summary>
    public DateTimeOffset? ExpectedUpdatedAt { get; init; }
}

/// <param name="Days">往后挪了几天。</param>
public sealed record ShiftedWorkItemDto(Guid Id, string Key, string Title, DateOnly? StartDate, DateOnly? DueDate, int Days);

/// <param name="Shifted">被顺延的后续任务，按依赖顺序排列。</param>
public sealed record ScheduleWorkItemResultDto(WorkItemDto Item, IReadOnlyList<ShiftedWorkItemDto> Shifted);
