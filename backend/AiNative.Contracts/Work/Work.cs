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

    public Guid? AssigneeAgentId { get; init; }

    public Guid? AssigneeUserId { get; init; }
}

public sealed record UpdateWorkItemRequest
{
    public required string Title { get; init; }

    public string Description { get; init; } = string.Empty;

    public required WorkItemStatus Status { get; init; }

    public required WorkItemPriority Priority { get; init; }

    public Guid? AssigneeAgentId { get; init; }

    public Guid? AssigneeUserId { get; init; }
}

public sealed record CommentDto(Guid Id, Guid WorkItemId, ActorDto Author, string Body, Guid? RunId, DateTimeOffset CreatedAt);

public sealed record AddCommentRequest
{
    public required string Body { get; init; }
}
