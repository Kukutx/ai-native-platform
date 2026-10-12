using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Common;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Common;
using AiNative.Contracts.Projects;
using AiNative.Domain.Projects;
using AiNative.Domain.Work;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Projects;

[RequireFeature(ProjectFeatures.View)]
public sealed record ListProjectsQuery(string? Search = null, ProjectStatus? Status = null, bool OpenOnly = false, int Page = 1, int PageSize = 50)
    : IQuery<PagedResult<ProjectDto>>;

[RequireFeature(ProjectFeatures.View)]
public sealed record GetProjectQuery(Guid Id) : IQuery<ProjectDetailDto>;

/// <param name="Milestones">一并创建的里程碑，按给出的顺序排列。</param>
[RequireFeature(ProjectFeatures.Manage)]
public sealed record CreateProjectCommand(SaveProjectRequest Request, IReadOnlyList<SaveProjectMilestoneRequest>? Milestones = null) : ICommand<ProjectDetailDto>;

[RequireFeature(ProjectFeatures.Manage)]
public sealed record UpdateProjectCommand(Guid Id, SaveProjectRequest Request) : ICommand<ProjectDto>;

[RequireFeature(ProjectFeatures.Manage)]
public sealed record DeleteProjectCommand(Guid Id) : ICommand<Unit>;

[RequireFeature(ProjectFeatures.Manage)]
public sealed record AddProjectMilestoneCommand(Guid ProjectId, SaveProjectMilestoneRequest Request) : ICommand<ProjectMilestoneDto>;

[RequireFeature(ProjectFeatures.Manage)]
public sealed record UpdateProjectMilestoneCommand(Guid ProjectId, Guid MilestoneId, SaveProjectMilestoneRequest Request) : ICommand<ProjectMilestoneDto>;

[RequireFeature(ProjectFeatures.Manage)]
public sealed record DeleteProjectMilestoneCommand(Guid ProjectId, Guid MilestoneId) : ICommand<Unit>;

/// <summary>
/// 标记里程碑完成或重新打开。完成要求里程碑下的任务都已关闭。
/// </summary>
[RequireFeature(ProjectFeatures.Manage)]
public sealed record SetProjectMilestoneCompletedCommand(Guid ProjectId, Guid MilestoneId, bool Completed) : ICommand<ProjectMilestoneDto>;

[RequireFeature(ProjectFeatures.Manage)]
public sealed record AddProjectMemberCommand(Guid ProjectId, AddProjectMemberRequest Request) : ICommand<ProjectMemberDto>;

[RequireFeature(ProjectFeatures.Manage)]
public sealed record UpdateProjectMemberCommand(Guid ProjectId, Guid MemberId, UpdateProjectMemberRequest Request) : ICommand<ProjectMemberDto>;

[RequireFeature(ProjectFeatures.Manage)]
public sealed record RemoveProjectMemberCommand(Guid ProjectId, Guid MemberId) : ICommand<Unit>;

public sealed class SaveProjectRequestValidator : AbstractValidator<SaveProjectRequest>
{
    public SaveProjectRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("请填写项目名称。").Text(100, "项目名称").OverridePropertyName("name");
        RuleFor(x => x.Description).Text(10_000, "项目说明").OverridePropertyName("description");
        RuleFor(x => x.Status).IsInEnum().OverridePropertyName("status");
        RuleFor(x => x).Must(r => r.LeadUserId is null || r.LeadAgentId is null)
            .WithMessage("项目只能有一位负责人。").OverridePropertyName("leadUserId");
        RuleFor(x => x.TargetDate).Must((r, target) => target is null || r.StartDate is null || target >= r.StartDate)
            .WithMessage("目标日期不能早于开始日期。").OverridePropertyName("targetDate");
    }
}

public sealed class CreateProjectCommandValidator : AbstractValidator<CreateProjectCommand>
{
    public const int MaxMilestones = 50;

    public CreateProjectCommandValidator()
    {
        RuleFor(x => x.Request).SetValidator(new SaveProjectRequestValidator());
        RuleFor(x => x.Milestones).Must(m => m is null || m.Count <= MaxMilestones)
            .WithMessage($"一次最多创建 {MaxMilestones} 个里程碑。").OverridePropertyName("milestones");
        RuleForEach(x => x.Milestones).SetValidator(new SaveProjectMilestoneRequestValidator()).OverridePropertyName("milestones");
    }
}

public sealed class UpdateProjectCommandValidator : AbstractValidator<UpdateProjectCommand>
{
    public UpdateProjectCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveProjectRequestValidator());
}

public sealed class SaveProjectMilestoneRequestValidator : AbstractValidator<SaveProjectMilestoneRequest>
{
    public SaveProjectMilestoneRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("请填写里程碑名称。").Text(100, "里程碑名称").OverridePropertyName("name");
        RuleFor(x => x.Description).Text(2_000, "里程碑说明").OverridePropertyName("description");
    }
}

public sealed class AddProjectMilestoneCommandValidator : AbstractValidator<AddProjectMilestoneCommand>
{
    public AddProjectMilestoneCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveProjectMilestoneRequestValidator());
}

public sealed class UpdateProjectMilestoneCommandValidator : AbstractValidator<UpdateProjectMilestoneCommand>
{
    public UpdateProjectMilestoneCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveProjectMilestoneRequestValidator());
}

public sealed class AddProjectMemberCommandValidator : AbstractValidator<AddProjectMemberCommand>
{
    public AddProjectMemberCommandValidator()
    {
        RuleFor(x => x.Request).Must(r => (r.UserId is null) != (r.AgentId is null))
            .WithMessage("请选择一位成员或一个智能体。").OverridePropertyName("userId");
        RuleFor(x => x.Request.Responsibility).Text(100, "职责").OverridePropertyName("responsibility");
    }
}

public sealed class UpdateProjectMemberCommandValidator : AbstractValidator<UpdateProjectMemberCommand>
{
    public UpdateProjectMemberCommandValidator()
        => RuleFor(x => x.Request.Responsibility).Text(100, "职责").OverridePropertyName("responsibility");
}

internal sealed class ProjectHandlers(
    IAppDbContext db,
    ITenantContext tenant,
    ProjectRecords records,
    ProjectWorkflow workflow,
    ITextMatcher text,
    IChangeFeed changes,
    TimeProvider clock) :
    IRequestHandler<ListProjectsQuery, PagedResult<ProjectDto>>,
    IRequestHandler<GetProjectQuery, ProjectDetailDto>,
    IRequestHandler<CreateProjectCommand, ProjectDetailDto>,
    IRequestHandler<UpdateProjectCommand, ProjectDto>,
    IRequestHandler<DeleteProjectCommand, Unit>
{
    public async Task<PagedResult<ProjectDto>> HandleAsync(ListProjectsQuery request, CancellationToken cancellationToken)
    {
        var (page, pageSize) = ProjectRecords.Paging(request.Page, request.PageSize);
        var query = db.Projects.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            query = text.WhereContains(query, request.Search, p => p.Name, p => p.Description);
        }

        if (request.Status is { } status)
        {
            query = query.Where(p => p.Status == status);
        }

        if (request.OpenOnly)
        {
            query = query.Where(p => p.Status != ProjectStatus.Completed && p.Status != ProjectStatus.Cancelled);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        // 进行中的排前面，同状态按目标日期（越近越前，没有日期的在后）。
        var items = await query
            .OrderBy(p => p.Status == ProjectStatus.Active ? 0 : p.Status == ProjectStatus.Planned ? 1 : p.Status == ProjectStatus.OnHold ? 2 : 3)
            .ThenBy(p => p.TargetDate == null).ThenBy(p => p.TargetDate).ThenByDescending(p => p.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<ProjectDto>(await ProjectMapping.ProjectsAsync(db, items, records.Today, cancellationToken).ConfigureAwait(false), total, page, pageSize);
    }

    public async Task<ProjectDetailDto> HandleAsync(GetProjectQuery request, CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.Id, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("project", request.Id);
        return await records.DetailAsync(project, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectDetailDto> HandleAsync(CreateProjectCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        var name = r.Name.Trim();
        await records.EnsureNameAvailableAsync(name, null, cancellationToken).ConfigureAwait(false);
        await EnsureLeadAsync(r, cancellationToken).ConfigureAwait(false);
        var project = new Project { Name = name, CreatedBy = tenant.Actor };
        Apply(project, r);
        project.ChangeStatus(r.Status, clock.GetUtcNow());
        db.Projects.Add(project);
        var milestones = (request.Milestones ?? []).Select(m => ProjectRecords.NewMilestone(project.Id, m)).ToList();
        db.ProjectMilestones.AddRange(milestones);
        var lead = await AddLeadAsMemberAsync(project, cancellationToken).ConfigureAwait(false);
        await workflow.OnLeadAssignedAsync(project, null, tenant.Actor, cancellationToken).ConfigureAwait(false);
        changes.Publish(ProjectRealtime.Project, project.Id, RealtimeChangeKind.Created);

        // 还没保存，详情直接用内存里的实体组装。
        var detail = await records.DetailAsync(project, milestones, lead is null ? [] : [lead], cancellationToken).ConfigureAwait(false);
        await records.PublishAsync(
            ProjectEventKeys.ProjectCreated,
            project.Id,
            () => Task.FromResult(new Dictionary<string, object?> { ["project"] = detail.Project, ["milestones"] = detail.Milestones }),
            cancellationToken).ConfigureAwait(false);
        return detail;
    }

    public async Task<ProjectDto> HandleAsync(UpdateProjectCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        var project = await records.ProjectAsync(request.Id, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (r.ExpectedUpdatedAt is { } expected)
        {
            await OptimisticConcurrency.EnsureUnchangedAsync(
                db.Projects, project.Id, expected, now, "project.modified", "项目刚被其他人或智能体修改过，已刷新为最新内容，请确认后再保存。", cancellationToken)
                .ConfigureAwait(false);
        }

        var name = r.Name.Trim();
        if (!string.Equals(name, project.Name, StringComparison.OrdinalIgnoreCase))
        {
            await records.EnsureNameAvailableAsync(name, project.Id, cancellationToken).ConfigureAwait(false);
        }

        var previousStatus = project.Status;
        var previousLeadAgent = project.LeadAgentId;
        var leadChanged = r.LeadUserId != project.LeadUserId || r.LeadAgentId != project.LeadAgentId;
        if (leadChanged)
        {
            await EnsureLeadAsync(r, cancellationToken).ConfigureAwait(false);
        }

        if (r.Status == ProjectStatus.Completed && previousStatus != ProjectStatus.Completed)
        {
            await EnsureNoOpenTasksAsync(project, cancellationToken).ConfigureAwait(false);
        }

        project.Name = name;
        Apply(project, r);
        project.ChangeStatus(r.Status, now);

        // 显式写入，返回的 UpdatedAt 就是落库值，可直接作为下一次编辑的版本。
        project.UpdatedAt = now;
        if (leadChanged)
        {
            await AddLeadAsMemberAsync(project, cancellationToken).ConfigureAwait(false);
        }

        await workflow.OnLeadAssignedAsync(project, previousLeadAgent, tenant.Actor, cancellationToken).ConfigureAwait(false);
        changes.Publish(ProjectRealtime.Project, project.Id, RealtimeChangeKind.Updated);
        var dto = await records.ToDtoAsync(project, cancellationToken).ConfigureAwait(false);
        if (previousStatus != project.Status)
        {
            await records.PublishAsync(
                ProjectEventKeys.ProjectStatusChanged,
                project.Id,
                () => Task.FromResult(new Dictionary<string, object?> { ["project"] = dto, ["from"] = previousStatus, ["to"] = project.Status }),
                cancellationToken).ConfigureAwait(false);
        }

        return dto;
    }

    /// <summary>
    /// 删除项目：里程碑和成员一并删除，项目里的任务保留下来，只是不再属于任何项目。
    /// </summary>
    public async Task<Unit> HandleAsync(DeleteProjectCommand request, CancellationToken cancellationToken)
    {
        var project = await records.ProjectAsync(request.Id, cancellationToken).ConfigureAwait(false);
        await records.LockAsync(project.Id, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        await db.WorkItems.Where(w => w.ProjectId == project.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.ProjectId, (Guid?)null).SetProperty(w => w.MilestoneId, (Guid?)null).SetProperty(w => w.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        db.Projects.Remove(project);
        changes.Publish(ProjectRealtime.Project, project.Id, RealtimeChangeKind.Deleted);
        return Unit.Value;
    }

    private async Task EnsureLeadAsync(SaveProjectRequest r, CancellationToken cancellationToken)
    {
        await records.EnsureUserAsync(r.LeadUserId, cancellationToken).ConfigureAwait(false);
        await records.EnsureAgentAsync(r.LeadAgentId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 在项目锁下确认没有未关闭的任务，检查期间不会有新任务加入。
    /// </summary>
    private async Task EnsureNoOpenTasksAsync(Project project, CancellationToken cancellationToken)
    {
        await records.LockAsync(project.Id, cancellationToken).ConfigureAwait(false);
        var open = await db.WorkItems.CountAsync(
            w => w.ProjectId == project.Id && w.Status != WorkItemStatus.Done && w.Status != WorkItemStatus.Cancelled,
            cancellationToken).ConfigureAwait(false);
        if (open > 0)
        {
            throw new ConflictException("project.open_tasks", $"项目里还有 {open} 个未关闭的任务，全部完成或取消后才能把项目标记为完成。");
        }
    }

    /// <summary>
    /// 负责人不在成员里时补进去（职责留空，界面按项目的负责人标出，换负责人后不会留下过时的说明）。项目锁下查重，和并发添加成员不冲突。
    /// </summary>
    private async Task<ProjectMember?> AddLeadAsMemberAsync(Project project, CancellationToken cancellationToken)
    {
        if (ProjectMapping.LeadOf(project) is not { } lead)
        {
            return null;
        }

        await records.LockAsync(project.Id, cancellationToken).ConfigureAwait(false);
        var members = db.ProjectMembers.Where(m => m.ProjectId == project.Id);
        var exists = lead.IsAgent
            ? await members.AnyAsync(m => m.AgentId == lead.Id, cancellationToken).ConfigureAwait(false)
            : await members.AnyAsync(m => m.UserId == lead.Id, cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return null;
        }

        var member = new ProjectMember
        {
            ProjectId = project.Id,
            UserId = project.LeadUserId,
            AgentId = project.LeadAgentId,
        };
        db.ProjectMembers.Add(member);
        return member;
    }

    private static void Apply(Project project, SaveProjectRequest r)
    {
        project.Description = r.Description.Trim();
        project.StartDate = r.StartDate;
        project.TargetDate = r.TargetDate;
        project.SetLead(r.LeadUserId, r.LeadAgentId);
    }
}

internal sealed class ProjectMilestoneHandlers(IAppDbContext db, ProjectRecords records, IChangeFeed changes, TimeProvider clock) :
    IRequestHandler<AddProjectMilestoneCommand, ProjectMilestoneDto>,
    IRequestHandler<UpdateProjectMilestoneCommand, ProjectMilestoneDto>,
    IRequestHandler<DeleteProjectMilestoneCommand, Unit>,
    IRequestHandler<SetProjectMilestoneCompletedCommand, ProjectMilestoneDto>
{
    public async Task<ProjectMilestoneDto> HandleAsync(AddProjectMilestoneCommand request, CancellationToken cancellationToken)
    {
        var project = await OpenProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        var milestone = ProjectRecords.NewMilestone(project.Id, request.Request);
        db.ProjectMilestones.Add(milestone);
        Touch(project);
        return await records.ToDtoAsync(milestone, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectMilestoneDto> HandleAsync(UpdateProjectMilestoneCommand request, CancellationToken cancellationToken)
    {
        var project = await OpenProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        var milestone = await records.MilestoneAsync(project.Id, request.MilestoneId, cancellationToken).ConfigureAwait(false);
        ProjectRecords.Apply(milestone, request.Request);
        milestone.UpdatedAt = clock.GetUtcNow();
        Touch(project);
        return await records.ToDtoAsync(milestone, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 删除里程碑：里面的任务留在项目里，只是不再属于这个里程碑。
    /// </summary>
    public async Task<Unit> HandleAsync(DeleteProjectMilestoneCommand request, CancellationToken cancellationToken)
    {
        var project = await OpenProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        var milestone = await records.MilestoneAsync(project.Id, request.MilestoneId, cancellationToken).ConfigureAwait(false);
        await records.LockAsync(project.Id, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        await db.WorkItems.Where(w => w.MilestoneId == milestone.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.MilestoneId, (Guid?)null).SetProperty(w => w.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        db.ProjectMilestones.Remove(milestone);
        Touch(project);
        return Unit.Value;
    }

    public async Task<ProjectMilestoneDto> HandleAsync(SetProjectMilestoneCompletedCommand request, CancellationToken cancellationToken)
    {
        var project = await OpenProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        var milestone = await records.MilestoneAsync(project.Id, request.MilestoneId, cancellationToken).ConfigureAwait(false);
        if (milestone.IsCompleted == request.Completed)
        {
            return await records.ToDtoAsync(milestone, cancellationToken).ConfigureAwait(false);
        }

        var now = clock.GetUtcNow();
        if (request.Completed)
        {
            // 在项目锁下检查，检查期间不会有新任务加入这个里程碑。
            await records.LockAsync(project.Id, cancellationToken).ConfigureAwait(false);
            var open = await db.WorkItems.CountAsync(
                w => w.MilestoneId == milestone.Id && w.Status != WorkItemStatus.Done && w.Status != WorkItemStatus.Cancelled,
                cancellationToken).ConfigureAwait(false);
            if (open > 0)
            {
                throw new ConflictException("project_milestone.open_tasks", $"里程碑「{milestone.Name}」下还有 {open} 个未关闭的任务，全部完成或取消后才能标记完成。");
            }
        }

        milestone.CompletedAt = request.Completed ? now : null;
        milestone.UpdatedAt = now;
        Touch(project);
        var dto = await records.ToDtoAsync(milestone, cancellationToken).ConfigureAwait(false);
        if (request.Completed)
        {
            var projectDto = await records.ToDtoAsync(project, cancellationToken).ConfigureAwait(false);
            await records.PublishAsync(
                ProjectEventKeys.MilestoneCompleted,
                milestone.Id,
                () => Task.FromResult(new Dictionary<string, object?> { ["project"] = projectDto, ["milestone"] = dto }),
                cancellationToken).ConfigureAwait(false);
        }

        return dto;
    }

    /// <summary>
    /// 已结束（完成或取消）的项目不能再调整里程碑，需要的话先把项目改回进行中。
    /// </summary>
    private async Task<Project> OpenProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await records.ProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project.IsClosed)
        {
            throw new ConflictException("project.closed", $"项目「{project.Name}」已经结束，不能再调整里程碑。需要的话先把项目改回进行中。");
        }

        return project;
    }

    /// <summary>
    /// 只通知客户端刷新，不推进项目的 UpdatedAt：调整里程碑和成员不应让正在编辑项目信息的人遇到冲突。
    /// </summary>
    private void Touch(Project project) => changes.Publish(ProjectRealtime.Project, project.Id, RealtimeChangeKind.Updated);
}

internal sealed class ProjectMemberHandlers(IAppDbContext db, ProjectRecords records, IChangeFeed changes, TimeProvider clock) :
    IRequestHandler<AddProjectMemberCommand, ProjectMemberDto>,
    IRequestHandler<UpdateProjectMemberCommand, ProjectMemberDto>,
    IRequestHandler<RemoveProjectMemberCommand, Unit>
{
    public async Task<ProjectMemberDto> HandleAsync(AddProjectMemberCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        var project = await records.ProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        await records.EnsureUserAsync(r.UserId, cancellationToken).ConfigureAwait(false);
        await records.EnsureAgentAsync(r.AgentId, cancellationToken).ConfigureAwait(false);

        // 项目锁下查重，并发添加同一位成员只有一个成功（唯一索引兜底）。
        await records.LockAsync(project.Id, cancellationToken).ConfigureAwait(false);
        var exists = await db.ProjectMembers.AnyAsync(
            m => m.ProjectId == project.Id && ((r.UserId != null && m.UserId == r.UserId) || (r.AgentId != null && m.AgentId == r.AgentId)),
            cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            throw new ConflictException("project_member.duplicate", "TA 已经是这个项目的成员了。");
        }

        var member = new ProjectMember { ProjectId = project.Id, UserId = r.UserId, AgentId = r.AgentId, Responsibility = r.Responsibility.Trim() };
        db.ProjectMembers.Add(member);
        Touch(project);
        return (await ProjectMapping.MembersAsync(db, project.Id, [member], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<ProjectMemberDto> HandleAsync(UpdateProjectMemberCommand request, CancellationToken cancellationToken)
    {
        var project = await records.ProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        var member = await MemberAsync(project.Id, request.MemberId, cancellationToken).ConfigureAwait(false);
        member.Responsibility = request.Request.Responsibility.Trim();
        member.UpdatedAt = clock.GetUtcNow();
        Touch(project);
        return (await ProjectMapping.MembersAsync(db, project.Id, [member], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<Unit> HandleAsync(RemoveProjectMemberCommand request, CancellationToken cancellationToken)
    {
        var project = await records.ProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        var member = await MemberAsync(project.Id, request.MemberId, cancellationToken).ConfigureAwait(false);
        if ((member.UserId is not null && member.UserId == project.LeadUserId) || (member.AgentId is not null && member.AgentId == project.LeadAgentId))
        {
            throw new ConflictException("project_member.is_lead", "TA 是项目负责人，先更换负责人再移出项目。");
        }

        db.ProjectMembers.Remove(member);
        Touch(project);
        return Unit.Value;
    }

    private async Task<ProjectMember> MemberAsync(Guid projectId, Guid memberId, CancellationToken cancellationToken)
        => await db.ProjectMembers.FirstOrDefaultAsync(m => m.Id == memberId && m.ProjectId == projectId, cancellationToken).ConfigureAwait(false)
           ?? throw new NotFoundException("project_member", memberId);

    /// <summary>
    /// 只通知客户端刷新，不推进项目的 UpdatedAt：调整里程碑和成员不应让正在编辑项目信息的人遇到冲突。
    /// </summary>
    private void Touch(Project project) => changes.Publish(ProjectRealtime.Project, project.Id, RealtimeChangeKind.Updated);
}
