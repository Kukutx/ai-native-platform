using AiNative.Domain.Agents;

namespace AiNative.Contracts.Agents;

public sealed record AgentDto(
    Guid Id,
    string Name,
    string Title,
    string Instructions,
    Guid? ReportsToId,
    AgentStatus Status,
    string? PauseReason,
    string ModelProvider,
    string Model,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> ApprovalRequiredActions,
    int HeartbeatIntervalSeconds,
    DateTimeOffset? LastHeartbeatAt,
    decimal MonthlyBudgetUsd,
    decimal SpentThisMonthUsd,
    string? LastError,
    DateTimeOffset CreatedAt);

public sealed record SaveAgentRequest
{
    public required string Name { get; init; }

    public required string Title { get; init; }

    public string Instructions { get; init; } = string.Empty;

    public Guid? ReportsToId { get; init; }

    /// <summary>
    /// 为空时使用平台默认模型。
    /// </summary>
    public string? ModelProvider { get; init; }

    public string? Model { get; init; }

    /// <summary>
    /// 为空时授予默认权限（查看团队、管理任务）。
    /// </summary>
    public IReadOnlyList<string>? Permissions { get; init; }

    public IReadOnlyList<string> ApprovalRequiredActions { get; init; } = [];

    public int HeartbeatIntervalSeconds { get; init; }

    public decimal MonthlyBudgetUsd { get; init; }
}

public sealed record OrgChartNodeDto(AgentDto Agent, IReadOnlyList<OrgChartNodeDto> Reports);

public sealed record WakeAgentRequest
{
    public string Reason { get; init; } = "手动唤醒";

    public Guid? WorkItemId { get; init; }
}

public sealed record WakeupItemDto(WakeupSource Source, string Reason, Guid? WorkItemId, Guid? ApprovalId, DateTimeOffset RequestedAt);

public sealed record WakeupDto(
    Guid Id,
    Guid AgentId,
    WakeupStatus Status,
    IReadOnlyList<WakeupItemDto> Items,
    int CoalescedCount,
    Guid? RunId,
    DateTimeOffset CreatedAt);

public sealed record RunDto(
    Guid Id,
    Guid AgentId,
    string AgentName,
    Guid? WakeupRequestId,
    Guid? WorkItemId,
    RunStatus Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string Provider,
    string Model,
    long InputTokens,
    long OutputTokens,
    decimal CostUsd,
    int Steps,
    string? Summary,
    string? Error,
    DateTimeOffset CreatedAt);

public sealed record RunDetailDto(RunDto Run, IReadOnlyList<RunEventDto> Events, WakeupDto? Wakeup);
