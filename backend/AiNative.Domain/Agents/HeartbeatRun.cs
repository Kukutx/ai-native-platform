using AiNative.Domain.Common;

namespace AiNative.Domain.Agents;

public enum RunStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
    TimedOut = 5,
}

/// <summary>
/// 智能体的一轮执行（心跳）。记录用量、费用、步骤数与结果摘要，过程明细在 RunEvent 中。
/// </summary>
public sealed class HeartbeatRun : TenantEntity
{
    public Guid AgentId { get; set; }

    public Guid? WakeupRequestId { get; set; }

    /// <summary>
    /// 本轮聚焦的工作项（由唤醒原因带入并已成功领取）。
    /// </summary>
    public Guid? WorkItemId { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Queued;

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public string Provider { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public decimal CostUsd { get; set; }

    public int Steps { get; set; }

    public string? Summary { get; set; }

    public string? Error { get; set; }

    public bool IsFinished => Status is RunStatus.Succeeded or RunStatus.Failed or RunStatus.Cancelled or RunStatus.TimedOut;
}

public enum RunEventType
{
    Status = 0,
    AssistantText = 1,
    ToolCall = 2,
    ToolResult = 3,
    ApprovalRequested = 4,
    Error = 5,
}

/// <summary>
/// 执行过程中的事件流水，按 Seq 有序；同时通过实时通道推送给工作台。
/// </summary>
public sealed class RunEvent : TenantEntity
{
    public Guid RunId { get; set; }

    public int Seq { get; set; }

    public RunEventType Type { get; set; }

    public string DataJson { get; set; } = "{}";
}
