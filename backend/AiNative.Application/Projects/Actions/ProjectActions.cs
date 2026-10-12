using System.ComponentModel;
using AiNative.Application.Abstractions;
using AiNative.Application.Actions;
using AiNative.Application.Common;
using AiNative.Contracts.Projects;
using AiNative.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Projects.Actions;

public sealed class ListProjectsInput
{
    [Description("项目名称或说明里的关键字；留空列出全部")]
    public string? Search { get; init; }

    [Description("按状态过滤：planned 计划中、active 进行中、on_hold 暂停、completed 已完成、cancelled 已取消")]
    public ProjectStatus? Status { get; init; }

    [Description("为 true 时也列出已完成和已取消的项目，默认只看未结束的")]
    public bool IncludeClosed { get; init; }

    [Description("最多返回条数，默认 20")]
    public int Limit { get; init; } = 20;
}

internal sealed class ListProjectsAction(IRequestDispatcher dispatcher) : PlatformAction<ListProjectsInput, IReadOnlyList<ProjectDto>>
{
    public override string Key => "projects.list";

    public override string ModuleKey => ProjectsModule.ModuleKey;

    public override string Title => "查询项目";

    public override string Description => "查询项目列表，返回负责人、起止日期、是否逾期，以及任务进度（总数、已完成、进行中、阻塞、逾期、完成百分比）。回答「项目进展如何」「哪些项目延期了」时使用。";

    public override string RequiredFeature => ProjectFeatures.View;

    public override bool IsMutation => false;

    protected override async Task<IReadOnlyList<ProjectDto>> ExecuteAsync(ListProjectsInput input, ActionContext context, CancellationToken cancellationToken)
        => (await dispatcher.SendAsync(
            new ListProjectsQuery(input.Search, input.Status, !input.IncludeClosed && input.Status is null, 1, Math.Clamp(input.Limit, 1, 100)),
            cancellationToken).ConfigureAwait(false)).Items;
}

public sealed class GetProjectInput
{
    [Description("项目名称（完整名称，不区分大小写）或项目 ID")]
    public required string Project { get; init; }
}

internal sealed class GetProjectAction(IRequestDispatcher dispatcher, ProjectRefs refs) : PlatformAction<GetProjectInput, ProjectDetailDto>
{
    public override string Key => "projects.get";

    public override string ModuleKey => ProjectsModule.ModuleKey;

    public override string Title => "查看项目详情";

    public override string Description => "查看一个项目的详情：进度、各里程碑的截止日期与进度、成员及其职责和未关闭任务数。项目里的具体任务用 tasks.list 加 projectId 查询。";

    public override string RequiredFeature => ProjectFeatures.View;

    public override bool IsMutation => false;

    protected override async Task<ProjectDetailDto> ExecuteAsync(GetProjectInput input, ActionContext context, CancellationToken cancellationToken)
        => await dispatcher.SendAsync(new GetProjectQuery(await refs.ProjectIdAsync(input.Project, cancellationToken).ConfigureAwait(false)), cancellationToken)
            .ConfigureAwait(false);
}

internal sealed class GetProjectScheduleAction(IRequestDispatcher dispatcher, ProjectRefs refs) : PlatformAction<GetProjectInput, ProjectScheduleDto>
{
    public override string Key => "projects.get_schedule";

    public override string ModuleKey => ProjectsModule.ModuleKey;

    public override string Title => "查看项目排期";

    public override string Description => "查看项目排期（甘特图数据）：每个任务的开始和截止日期、负责人、还在等几个前置任务（waitingOn）、是否在关键路径上（critical），以及项目内的依赖（links，conflict 为 true 表示后续任务开始得比前置任务截止还早）。规划或检查项目进度时使用；发现冲突可以用 tasks.schedule 加 shiftDependents 顺延后续任务。";

    public override string RequiredFeature => ProjectFeatures.View;

    public override bool IsMutation => false;

    protected override async Task<ProjectScheduleDto> ExecuteAsync(GetProjectInput input, ActionContext context, CancellationToken cancellationToken)
        => await dispatcher.SendAsync(new GetProjectScheduleQuery(await refs.ProjectIdAsync(input.Project, cancellationToken).ConfigureAwait(false)), cancellationToken)
            .ConfigureAwait(false);
}

public sealed class ProjectMilestoneInput
{
    [Description("里程碑名称，例如「完成设计稿」")]
    public required string Name { get; init; }

    [Description("完成标准或说明")]
    public string Description { get; init; } = string.Empty;

    [Description("截止日期，格式 yyyy-MM-dd")]
    public DateOnly? DueDate { get; init; }
}

public sealed class CreateProjectInput
{
    [Description("项目名称，空间内唯一")]
    public required string Name { get; init; }

    [Description("项目目标、范围和验收标准")]
    public string Description { get; init; } = string.Empty;

    [Description("负责的智能体 ID；与 leadUserId 都省略时由调用者自己负责")]
    public Guid? LeadAgentId { get; init; }

    [Description("负责的成员（用户）ID")]
    public Guid? LeadUserId { get; init; }

    [Description("开始日期，格式 yyyy-MM-dd")]
    public DateOnly? StartDate { get; init; }

    [Description("目标完成日期，格式 yyyy-MM-dd")]
    public DateOnly? TargetDate { get; init; }

    [Description("初始状态，默认 active（进行中）；还没开始可以用 planned")]
    public ProjectStatus Status { get; init; } = ProjectStatus.Active;

    [Description("一并创建的里程碑，按时间顺序列出")]
    public IReadOnlyList<ProjectMilestoneInput> Milestones { get; init; } = [];
}

internal sealed class CreateProjectAction(IRequestDispatcher dispatcher) : PlatformAction<CreateProjectInput, ProjectDetailDto>
{
    public override string Key => "projects.create";

    public override string ModuleKey => ProjectsModule.ModuleKey;

    public override string Title => "创建项目";

    public override string Description => "创建项目，可同时列出里程碑。负责人可以是成员或智能体，智能体负责人会被唤醒去规划任务。之后用 tasks.create 加 projectId / milestoneId 把任务放进项目。";

    public override string RequiredFeature => ProjectFeatures.Manage;

    protected override async Task<ProjectDetailDto> ExecuteAsync(CreateProjectInput input, ActionContext context, CancellationToken cancellationToken)
    {
        var (leadUser, leadAgent) = (input.LeadUserId, input.LeadAgentId);
        if (leadUser is null && leadAgent is null)
        {
            leadUser = context.Actor.IsUser ? context.Actor.Id : null;
            leadAgent = context.Actor.IsAgent ? context.Actor.Id : null;
        }

        return await dispatcher.SendAsync(
            new CreateProjectCommand(
                new SaveProjectRequest
                {
                    Name = input.Name,
                    Description = input.Description,
                    Status = input.Status,
                    LeadUserId = leadUser,
                    LeadAgentId = leadAgent,
                    StartDate = input.StartDate,
                    TargetDate = input.TargetDate,
                },
                [.. input.Milestones.Select(m => new SaveProjectMilestoneRequest { Name = m.Name, Description = m.Description, DueDate = m.DueDate })]),
            cancellationToken).ConfigureAwait(false);
    }
}

public sealed class UpdateProjectInput
{
    [Description("项目名称或项目 ID")]
    public required string Project { get; init; }

    [Description("新名称；不改就省略")]
    public string? Name { get; init; }

    [Description("新的目标与说明；不改就省略")]
    public string? Description { get; init; }

    [Description("新状态：planned、active、on_hold、completed（要求项目里的任务都已关闭）、cancelled")]
    public ProjectStatus? Status { get; init; }

    [Description("改由这个智能体负责")]
    public Guid? LeadAgentId { get; init; }

    [Description("改由这位成员负责")]
    public Guid? LeadUserId { get; init; }

    [Description("新的开始日期，格式 yyyy-MM-dd")]
    public DateOnly? StartDate { get; init; }

    [Description("新的目标完成日期，格式 yyyy-MM-dd")]
    public DateOnly? TargetDate { get; init; }
}

internal sealed class UpdateProjectAction(IRequestDispatcher dispatcher, ProjectRefs refs) : PlatformAction<UpdateProjectInput, ProjectDto>
{
    public override string Key => "projects.update";

    public override string ModuleKey => ProjectsModule.ModuleKey;

    public override string Title => "更新项目";

    public override string Description => "修改项目的名称、说明、状态、负责人或日期，只需填写要改的字段。项目任务全部关闭后可以把状态改为 completed；暂停用 on_hold。";

    public override string RequiredFeature => ProjectFeatures.Manage;

    protected override async Task<ProjectDto> ExecuteAsync(UpdateProjectInput input, ActionContext context, CancellationToken cancellationToken)
    {
        var current = await refs.ProjectAsync(input.Project, cancellationToken).ConfigureAwait(false);
        var (leadUser, leadAgent) = input.LeadAgentId is not null || input.LeadUserId is not null
            ? (input.LeadUserId, input.LeadAgentId)
            : (current.LeadUserId, current.LeadAgentId);
        return await dispatcher.SendAsync(
            new UpdateProjectCommand(current.Id, new SaveProjectRequest
            {
                Name = input.Name ?? current.Name,
                Description = input.Description ?? current.Description,
                Status = input.Status ?? current.Status,
                LeadUserId = leadUser,
                LeadAgentId = leadAgent,
                StartDate = input.StartDate ?? current.StartDate,
                TargetDate = input.TargetDate ?? current.TargetDate,

                // 基于读到的版本修改，期间被别人改过就返回冲突，不会覆盖别人的修改。
                ExpectedUpdatedAt = current.UpdatedAt,
            }),
            cancellationToken).ConfigureAwait(false);
    }
}

public sealed class AddProjectMilestoneInput
{
    [Description("项目名称或项目 ID")]
    public required string Project { get; init; }

    [Description("里程碑名称")]
    public required string Name { get; init; }

    [Description("完成标准或说明")]
    public string Description { get; init; } = string.Empty;

    [Description("截止日期，格式 yyyy-MM-dd")]
    public DateOnly? DueDate { get; init; }
}

internal sealed class AddProjectMilestoneAction(IRequestDispatcher dispatcher, ProjectRefs refs) : PlatformAction<AddProjectMilestoneInput, ProjectMilestoneDto>
{
    public override string Key => "projects.add_milestone";

    public override string ModuleKey => ProjectsModule.ModuleKey;

    public override string Title => "添加里程碑";

    public override string Description => "给项目添加一个里程碑（阶段性目标）。已结束的项目不能添加。";

    public override string RequiredFeature => ProjectFeatures.Manage;

    protected override async Task<ProjectMilestoneDto> ExecuteAsync(AddProjectMilestoneInput input, ActionContext context, CancellationToken cancellationToken)
        => await dispatcher.SendAsync(
            new AddProjectMilestoneCommand(
                await refs.ProjectIdAsync(input.Project, cancellationToken).ConfigureAwait(false),
                new SaveProjectMilestoneRequest { Name = input.Name, Description = input.Description, DueDate = input.DueDate }),
            cancellationToken).ConfigureAwait(false);
}

public sealed class CompleteProjectMilestoneInput
{
    [Description("项目名称或项目 ID")]
    public required string Project { get; init; }

    [Description("里程碑名称或里程碑 ID")]
    public required string Milestone { get; init; }
}

internal sealed class CompleteProjectMilestoneAction(IRequestDispatcher dispatcher, ProjectRefs refs) : PlatformAction<CompleteProjectMilestoneInput, ProjectMilestoneDto>
{
    public override string Key => "projects.complete_milestone";

    public override string ModuleKey => ProjectsModule.ModuleKey;

    public override string Title => "完成里程碑";

    public override string Description => "验收后把里程碑标记为完成。要求里程碑下的任务都已完成或取消，否则会提示还有几个未关闭。";

    public override string RequiredFeature => ProjectFeatures.Manage;

    protected override async Task<ProjectMilestoneDto> ExecuteAsync(CompleteProjectMilestoneInput input, ActionContext context, CancellationToken cancellationToken)
    {
        var projectId = await refs.ProjectIdAsync(input.Project, cancellationToken).ConfigureAwait(false);
        var milestoneId = await refs.MilestoneIdAsync(projectId, input.Milestone, cancellationToken).ConfigureAwait(false);
        return await dispatcher.SendAsync(new SetProjectMilestoneCompletedCommand(projectId, milestoneId, true), cancellationToken).ConfigureAwait(false);
    }
}

public sealed class AddProjectMemberInput
{
    [Description("项目名称或项目 ID")]
    public required string Project { get; init; }

    [Description("加入的智能体 ID，与 userId 二选一")]
    public Guid? AgentId { get; init; }

    [Description("加入的成员（用户）ID")]
    public Guid? UserId { get; init; }

    [Description("在项目里的职责，例如「前端开发」「测试」")]
    public string Responsibility { get; init; } = string.Empty;
}

internal sealed class AddProjectMemberAction(IRequestDispatcher dispatcher, ProjectRefs refs) : PlatformAction<AddProjectMemberInput, ProjectMemberDto>
{
    public override string Key => "projects.add_member";

    public override string ModuleKey => ProjectsModule.ModuleKey;

    public override string Title => "添加项目成员";

    public override string Description => "把成员或智能体加入项目并写明职责，便于分工和统计每人的未完成任务。";

    public override string RequiredFeature => ProjectFeatures.Manage;

    protected override async Task<ProjectMemberDto> ExecuteAsync(AddProjectMemberInput input, ActionContext context, CancellationToken cancellationToken)
        => await dispatcher.SendAsync(
            new AddProjectMemberCommand(
                await refs.ProjectIdAsync(input.Project, cancellationToken).ConfigureAwait(false),
                new AddProjectMemberRequest { AgentId = input.AgentId, UserId = input.UserId, Responsibility = input.Responsibility }),
            cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// 动作参数里的项目、里程碑引用（名称或 ID）。直接查库解析，不经过需要查看权限的查询，
/// 动作只要求它自己声明的权限。
/// </summary>
internal sealed class ProjectRefs(IAppDbContext db)
{
    public async Task<Project> ProjectAsync(string reference, CancellationToken cancellationToken)
    {
        var id = await ProjectIdAsync(reference, cancellationToken).ConfigureAwait(false);
        return await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken).ConfigureAwait(false)
               ?? throw new NotFoundException("project", id);
    }

    public async Task<Guid> ProjectIdAsync(string reference, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(reference, out var id))
        {
            return id;
        }

        var name = reference.Trim().ToLowerInvariant();
        return await db.Projects.AsNoTracking().Where(p => p.Name.ToLower() == name).Select(p => (Guid?)p.Id)
                   .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
               ?? throw Invalid("project", $"找不到名为「{reference.Trim()}」的项目，可以先用 projects.list 搜索。");
    }

    public async Task<Guid> MilestoneIdAsync(Guid projectId, string reference, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(reference, out var id))
        {
            return id;
        }

        var milestones = await db.ProjectMilestones.AsNoTracking().Where(m => m.ProjectId == projectId).OrderBy(m => m.CreatedAt)
            .Select(m => new { m.Id, m.Name }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var wanted = reference.Trim();
        return milestones.FirstOrDefault(m => string.Equals(m.Name, wanted, StringComparison.OrdinalIgnoreCase))?.Id
               ?? throw Invalid(
                   "milestone",
                   milestones.Count == 0 ? "这个项目还没有里程碑。" : $"项目里没有名为「{wanted}」的里程碑，可选：{string.Join("、", milestones.Select(m => m.Name))}。");
    }

    private static ValidationFailedException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}
