using AiNative.Domain.Common;

namespace AiNative.Domain.Workflows;

public enum WorkflowRunStatus
{
    Queued = 0,
    Running = 1,

    /// <summary>
    /// 等待外部条件：审批结论、智能体完成任务或延时到期。
    /// </summary>
    Waiting = 2,
    Succeeded = 3,
    Failed = 4,
    Cancelled = 5,
}

public enum WorkflowStepStatus
{
    Running = 0,
    Waiting = 1,
    Succeeded = 2,
    Failed = 3,
    Skipped = 4,
}

/// <summary>
/// 工作流的一次运行。创建时保存定义快照和运行身份，运行期间修改工作流不影响已经开始的运行。
/// 执行进度（CurrentStep）与每一步的副作用在同一事务里提交，进程崩溃后从中断的步骤继续。
/// </summary>
public sealed class WorkflowRun : TenantEntity
{
    public Guid WorkflowId { get; set; }

    public int WorkflowVersion { get; set; }

    public required string WorkflowName { get; set; }

    public List<WorkflowStep> Steps { get; set; } = [];

    /// <summary>
    /// 以谁的身份和权限执行（创建运行时工作流的所有者）。
    /// </summary>
    public Actor Owner { get; set; }

    /// <summary>
    /// 谁触发的：手动运行时是操作者，事件触发时是引发事件的人或智能体，定时触发为系统。
    /// </summary>
    public Actor TriggeredBy { get; set; }

    public WorkflowTriggerKind TriggerKind { get; set; }

    public string? EventType { get; set; }

    /// <summary>
    /// 触发数据（事件载荷或定时信息），表达式里通过 trigger 引用。
    /// </summary>
    public string TriggerJson { get; set; } = "{}";

    /// <summary>
    /// 手动运行时传入的参数，表达式里通过 input 引用。
    /// </summary>
    public string InputJson { get; set; } = "{}";

    public WorkflowRunStatus Status { get; set; } = WorkflowRunStatus.Queued;

    /// <summary>
    /// 下一个要执行（或正在等待）的步骤序号。
    /// </summary>
    public int CurrentStep { get; set; }

    /// <summary>
    /// 等待中的运行到这个时间会被重新调度：延时到期或等待超时。
    /// </summary>
    public DateTimeOffset? ResumeAt { get; set; }

    public Guid? WaitingApprovalId { get; set; }

    public Guid? WaitingWorkItemId { get; set; }

    /// <summary>
    /// 由另一个工作流运行中的步骤引发时记录来源，用于防止工作流互相触发形成循环。
    /// </summary>
    public Guid? ParentRunId { get; set; }

    public int Depth { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public bool IsFinished => Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed or WorkflowRunStatus.Cancelled;
}

/// <summary>
/// 运行中某一步的执行记录：解析后的输入、输出或错误。
/// </summary>
public sealed class WorkflowRunStep : TenantEntity
{
    public Guid RunId { get; set; }

    /// <summary>
    /// 所属的顶层步骤序号。
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// 步骤在定义树里的位置：顶层是序号（"2"）；分支或遍历里的子步骤是「父位置.分支序号或第几项.子步骤序号」（"2.1.0"）。
    /// 同一个子步骤在遍历的每一项里各有一条记录。
    /// </summary>
    public required string Path { get; set; }

    public required string StepKey { get; set; }

    public required string Name { get; set; }

    public WorkflowStepKind Kind { get; set; }

    public WorkflowStepStatus Status { get; set; }

    public string? InputJson { get; set; }

    public string? OutputJson { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}
