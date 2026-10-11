using AiNative.Domain.Common;

namespace AiNative.Domain.Projects;

public enum ProjectStatus
{
    Planned = 0,
    Active = 1,
    OnHold = 2,
    Completed = 3,
    Cancelled = 4,
}

/// <summary>
/// 项目：把一组任务围绕同一个目标组织起来，按里程碑推进。负责人可以是成员，也可以是智能体（项目经理）。
/// 任务通过 <c>WorkItem.ProjectId</c> 归属项目，进度由任务状态实时算出，不单独存。
/// </summary>
public sealed class Project : TenantEntity
{
    public required string Name { get; set; }

    /// <summary>
    /// 目标与范围。智能体处理项目里的任务时会读到它。
    /// </summary>
    public string Description { get; set; } = string.Empty;

    public ProjectStatus Status { get; private set; } = ProjectStatus.Active;

    public Guid? LeadUserId { get; private set; }

    public Guid? LeadAgentId { get; private set; }

    public DateOnly? StartDate { get; set; }

    public DateOnly? TargetDate { get; set; }

    public Actor CreatedBy { get; set; }

    /// <summary>
    /// 完成或取消的时间；重新打开时清空。
    /// </summary>
    public DateTimeOffset? ClosedAt { get; private set; }

    public bool IsClosed => Status is ProjectStatus.Completed or ProjectStatus.Cancelled;

    public void ChangeStatus(ProjectStatus status, DateTimeOffset now)
    {
        if (status == Status)
        {
            return;
        }

        Status = status;
        ClosedAt = IsClosed ? now : null;
    }

    /// <summary>
    /// 负责人只能有一个：成员或智能体。
    /// </summary>
    public void SetLead(Guid? userId, Guid? agentId)
    {
        if (userId is not null && agentId is not null)
        {
            throw new DomainException("project.single_lead", "项目只能有一位负责人。");
        }

        LeadUserId = userId;
        LeadAgentId = agentId;
    }
}

/// <summary>
/// 里程碑：项目里的阶段性目标，有截止日期。任务可以挂在里程碑下；完成里程碑要求它的任务都已关闭。
/// </summary>
public sealed class ProjectMilestone : TenantEntity
{
    public Guid ProjectId { get; set; }

    public required string Name { get; set; }

    public string Description { get; set; } = string.Empty;

    public DateOnly? DueDate { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public bool IsCompleted => CompletedAt is not null;
}

/// <summary>
/// 项目成员：成员或智能体，写明在项目里负责什么，方便人和智能体知道该找谁。
/// </summary>
public sealed class ProjectMember : TenantEntity
{
    public Guid ProjectId { get; set; }

    public Guid? UserId { get; set; }

    public Guid? AgentId { get; set; }

    public string Responsibility { get; set; } = string.Empty;
}
