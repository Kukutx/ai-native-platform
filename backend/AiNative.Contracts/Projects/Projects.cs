using AiNative.Contracts.Common;
using AiNative.Domain.Projects;

namespace AiNative.Contracts.Projects;

/// <summary>
/// 任务进度：已取消的任务不计入总数；逾期指截止日期已过、仍未关闭的任务。
/// </summary>
public sealed record ProjectProgressDto(int Total, int Done, int InProgress, int Blocked, int Overdue, int Percent);

/// <param name="IsOverdue">目标日期已过、项目仍未结束。</param>
public sealed record ProjectDto(
    Guid Id,
    string Name,
    string Description,
    ProjectStatus Status,
    ActorDto? Lead,
    DateOnly? StartDate,
    DateOnly? TargetDate,
    bool IsOverdue,
    ProjectProgressDto Progress,
    int MilestoneCount,
    int MemberCount,
    ActorDto CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ClosedAt);

public sealed record ProjectMilestoneDto(
    Guid Id,
    Guid ProjectId,
    string Name,
    string Description,
    DateOnly? DueDate,
    bool IsOverdue,
    DateTimeOffset? CompletedAt,
    ProjectProgressDto Progress,
    DateTimeOffset UpdatedAt);

public sealed record ProjectMemberDto(Guid Id, ActorDto Member, string Responsibility, int OpenTasks, DateTimeOffset CreatedAt);

/// <summary>
/// 项目详情：里程碑按截止日期排列（没有日期的在后），成员先人后智能体。任务用任务接口按 projectId 查询。
/// </summary>
public sealed record ProjectDetailDto(ProjectDto Project, IReadOnlyList<ProjectMilestoneDto> Milestones, IReadOnlyList<ProjectMemberDto> Members);

public sealed record SaveProjectRequest
{
    public required string Name { get; init; }

    public string Description { get; init; } = string.Empty;

    public ProjectStatus Status { get; init; } = ProjectStatus.Active;

    /// <summary>
    /// 负责人：成员或智能体，二选一。
    /// </summary>
    public Guid? LeadUserId { get; init; }

    public Guid? LeadAgentId { get; init; }

    public DateOnly? StartDate { get; init; }

    public DateOnly? TargetDate { get; init; }

    /// <summary>
    /// 编辑所基于的 <see cref="ProjectDto.UpdatedAt"/>。提供时，项目在此之后被改过就返回 409。
    /// </summary>
    public DateTimeOffset? ExpectedUpdatedAt { get; init; }
}

public sealed record SaveProjectMilestoneRequest
{
    public required string Name { get; init; }

    public string Description { get; init; } = string.Empty;

    public DateOnly? DueDate { get; init; }
}

public sealed record AddProjectMemberRequest
{
    /// <summary>
    /// 成员或智能体，二选一。
    /// </summary>
    public Guid? UserId { get; init; }

    public Guid? AgentId { get; init; }

    public string Responsibility { get; init; } = string.Empty;
}

public sealed record UpdateProjectMemberRequest
{
    public string Responsibility { get; init; } = string.Empty;
}
