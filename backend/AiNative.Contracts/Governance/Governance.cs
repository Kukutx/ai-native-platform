using System.Text.Json;
using AiNative.Contracts.Common;
using AiNative.Domain.Governance;

namespace AiNative.Contracts.Governance;

public sealed record ApprovalDto(
    Guid Id,
    string ActionKey,
    string ActionTitle,
    JsonElement Input,
    string? Rationale,
    ActorDto RequestedBy,
    Guid? RunId,
    Guid? WorkItemId,
    ApprovalStatus Status,
    Guid? DecidedByUserId,
    DateTimeOffset? DecidedAt,
    string? DecisionNote,
    JsonElement? Result,
    string? ExecutionError,
    DateTimeOffset CreatedAt);

public sealed record DecideApprovalRequest
{
    public required bool Approve { get; init; }

    public string? Note { get; init; }
}
