using AiNative.Domain.Common;

namespace AiNative.Domain.Governance;

public enum ApprovalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
}

/// <summary>
/// 人工审批：智能体想执行需要把关的动作时生成，批准后以该智能体身份执行并把结果回传给它。
/// </summary>
public sealed class Approval : TenantEntity
{
    public required string ActionKey { get; set; }

    public string InputJson { get; set; } = "{}";

    /// <summary>
    /// 智能体给出的执行理由，帮助审批人判断。
    /// </summary>
    public string? Rationale { get; set; }

    public Actor RequestedBy { get; set; }

    public Guid? RunId { get; set; }

    public Guid? WorkItemId { get; set; }

    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;

    public Guid? DecidedByUserId { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public string? DecisionNote { get; set; }

    public string? ResultJson { get; set; }

    public string? ExecutionError { get; set; }

    public void Decide(bool approve, Guid userId, string? note, DateTimeOffset now)
    {
        if (Status != ApprovalStatus.Pending)
        {
            throw new DomainException("approval.already_decided", "该审批已经处理过。");
        }

        Status = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        DecidedByUserId = userId;
        DecisionNote = note;
        DecidedAt = now;
    }
}
