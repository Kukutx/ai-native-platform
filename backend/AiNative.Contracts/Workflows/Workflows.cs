using System.Text.Json;
using AiNative.Contracts.Common;
using AiNative.Domain.Workflows;

namespace AiNative.Contracts.Workflows;

public sealed record WorkflowTriggerDto
{
    public WorkflowTriggerKind Kind { get; init; }

    /// <summary>
    /// 事件触发：事件键，例如 tasks.created。
    /// </summary>
    public string? EventType { get; init; }

    /// <summary>
    /// 事件触发：过滤条件表达式，例如 trigger.to == 'done'。
    /// </summary>
    public string? Filter { get; init; }

    /// <summary>
    /// 定时触发：5 段 cron（分 时 日 月 周），例如 "0 9 * * MON-FRI"。
    /// </summary>
    public string? Cron { get; init; }

    /// <summary>
    /// 定时触发：时区（IANA 名称），默认 UTC。
    /// </summary>
    public string? TimeZone { get; init; }
}

public sealed record WorkflowDto(
    Guid Id,
    string Name,
    string Description,
    bool IsEnabled,
    WorkflowTriggerDto Trigger,
    IReadOnlyList<WorkflowStep> Steps,
    ActorDto Owner,
    int Version,
    DateTimeOffset? NextRunAt,
    WorkflowRunSummaryDto? LastRun,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record WorkflowRunSummaryDto(
    Guid Id,
    Guid WorkflowId,
    string WorkflowName,
    WorkflowRunStatus Status,
    WorkflowTriggerKind TriggerKind,
    string? EventType,
    ActorDto TriggeredBy,
    int CurrentStep,
    int StepCount,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record WorkflowRunStepDto(
    int Index,
    string StepKey,
    string Name,
    WorkflowStepKind Kind,
    WorkflowStepStatus Status,
    JsonElement? Input,
    JsonElement? Output,
    string? Error,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record WorkflowRunDto(
    WorkflowRunSummaryDto Summary,
    ActorDto Owner,
    int WorkflowVersion,
    JsonElement Trigger,
    JsonElement Input,
    IReadOnlyList<WorkflowStep> Steps,
    IReadOnlyList<WorkflowRunStepDto> StepRuns,
    Guid? WaitingApprovalId,
    Guid? WaitingWorkItemId,
    DateTimeOffset? ResumeAt,
    Guid? ParentRunId);

public sealed record SaveWorkflowRequest
{
    public required string Name { get; init; }

    public string Description { get; init; } = string.Empty;

    public bool IsEnabled { get; init; } = true;

    public required WorkflowTriggerDto Trigger { get; init; }

    public required IReadOnlyList<WorkflowStep> Steps { get; init; }

    /// <summary>
    /// 编辑时回传读取到的 UpdatedAt；期间被别人改过则返回 409，避免覆盖。
    /// </summary>
    public DateTimeOffset? ExpectedUpdatedAt { get; init; }
}

public sealed record SetWorkflowEnabledRequest
{
    public required bool Enabled { get; init; }
}

public sealed record StartWorkflowRunRequest
{
    /// <summary>
    /// 运行参数，表达式里通过 input 引用。
    /// </summary>
    public JsonElement? Input { get; init; }
}

public sealed record WorkflowEventDto(string Key, string Name, string ModuleKey, string Description);

public sealed record WorkflowCatalogDto(IReadOnlyList<WorkflowEventDto> Events);
