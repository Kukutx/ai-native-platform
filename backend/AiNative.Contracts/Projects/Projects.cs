using AiNative.Contracts.Common;
using AiNative.Domain.Projects;
using AiNative.Domain.Work;

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

/// <summary>
/// 甘特图上的一个任务。只有截止日期时按一天的时间条显示；开始和截止都没有的算未排期。
/// </summary>
/// <param name="Assignee">负责人：成员或智能体。</param>
/// <param name="WaitingOn">还没关闭的前置任务数（包括其他项目里的前置任务）。</param>
/// <param name="Critical">在关键路径上：它晚一天，项目里最晚的截止日期就跟着晚一天。只算未关闭、有日期的任务。</param>
/// <param name="UpdatedAt">最后修改时间，调整日期时作为并发检查的依据。</param>
public sealed record ScheduledTaskDto(
    Guid Id,
    string Key,
    string Title,
    WorkItemStatus Status,
    WorkItemPriority Priority,
    Guid? MilestoneId,
    Guid? ParentId,
    ActorDto? Assignee,
    DateOnly? StartDate,
    DateOnly? DueDate,
    DateTimeOffset? CompletedAt,
    bool IsOverdue,
    int WaitingOn,
    bool Critical,
    DateTimeOffset UpdatedAt);

/// <summary>
/// 项目内的一条依赖：<see cref="ToId"/> 要等 <see cref="FromId"/> 关闭后才能开始。
/// </summary>
/// <param name="Conflict">排期冲突：两个任务都未关闭、都有日期，后续任务的开始日期早于前置任务的截止日期（同一天不算冲突）。</param>
public sealed record ScheduleLinkDto(Guid FromId, Guid ToId, bool Conflict);

/// <summary>
/// 项目排期（甘特图数据）。任务按里程碑顺序、再按开始日期排列；已取消的任务不列出。
/// </summary>
/// <param name="Start">时间轴起点：项目开始日期、任务日期和里程碑截止日期里最早的一个。</param>
/// <param name="End">时间轴终点：项目目标日期、任务日期和里程碑截止日期里最晚的一个。</param>
/// <param name="Conflicts">排期冲突的依赖条数。</param>
/// <param name="Unscheduled">开始和截止日期都没有的任务数。</param>
/// <param name="Truncated">任务太多只返回了一部分。</param>
public sealed record ProjectScheduleDto(
    ProjectDto Project,
    IReadOnlyList<ProjectMilestoneDto> Milestones,
    IReadOnlyList<ScheduledTaskDto> Tasks,
    IReadOnlyList<ScheduleLinkDto> Links,
    DateOnly? Start,
    DateOnly? End,
    int Conflicts,
    int Unscheduled,
    bool Truncated);
