using System.Linq.Expressions;
using AiNative.Application.Abstractions;
using AiNative.Application.Agents;
using AiNative.Application.Common;
using AiNative.Application.Modules;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Common;
using AiNative.Contracts.Projects;
using AiNative.Domain.Agents;
using AiNative.Domain.Common;
using AiNative.Domain.Projects;
using AiNative.Domain.Work;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Projects;

/// <summary>
/// 实体到 DTO 的批量映射：进度、计数和名称一次查齐，列表不逐条查询。进度按任务状态现算。
/// </summary>
internal static class ProjectMapping
{
    private static readonly ProjectProgressDto NoTasks = new(0, 0, 0, 0, 0, 0);

    public static async Task<IReadOnlyList<ProjectDto>> ProjectsAsync(IAppDbContext db, IReadOnlyList<Project> projects, DateOnly today, CancellationToken cancellationToken)
    {
        if (projects.Count == 0)
        {
            return [];
        }

        var ids = projects.Select(p => p.Id).ToList();
        var progress = await ProgressAsync(db.WorkItems.Where(w => w.ProjectId != null && ids.Contains(w.ProjectId.Value)), w => w.ProjectId!.Value, today, cancellationToken)
            .ConfigureAwait(false);
        var milestones = await db.ProjectMilestones.AsNoTracking().Where(m => ids.Contains(m.ProjectId))
            .GroupBy(m => m.ProjectId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken).ConfigureAwait(false);
        var members = await db.ProjectMembers.AsNoTracking().Where(m => ids.Contains(m.ProjectId))
            .GroupBy(m => m.ProjectId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken).ConfigureAwait(false);

        // 本次请求里新加、尚未保存的里程碑和成员也算进去，保存前返回的 DTO 同样准确。
        foreach (var added in db.ProjectMilestones.Local.Where(m => ids.Contains(m.ProjectId) && db.Entry(m).State == EntityState.Added))
        {
            milestones[added.ProjectId] = milestones.GetValueOrDefault(added.ProjectId) + 1;
        }

        foreach (var added in db.ProjectMembers.Local.Where(m => ids.Contains(m.ProjectId) && db.Entry(m).State == EntityState.Added))
        {
            members[added.ProjectId] = members.GetValueOrDefault(added.ProjectId) + 1;
        }

        var actors = await ActorNames.ResolveAsync(
            db,
            projects.Select(p => p.CreatedBy).Concat(projects.Select(LeadOf).Where(l => l.HasValue).Select(l => l!.Value)),
            cancellationToken).ConfigureAwait(false);
        return
        [
            .. projects.Select(p => new ProjectDto(
                p.Id,
                p.Name,
                p.Description,
                p.Status,
                LeadOf(p) is { } lead ? actors(lead) : null,
                p.StartDate,
                p.TargetDate,
                !p.IsClosed && p.TargetDate < today,
                progress.GetValueOrDefault(p.Id, NoTasks),
                milestones.GetValueOrDefault(p.Id),
                members.GetValueOrDefault(p.Id),
                actors(p.CreatedBy),
                p.CreatedAt,
                p.UpdatedAt,
                p.ClosedAt)),
        ];
    }

    public static async Task<IReadOnlyList<ProjectMilestoneDto>> MilestonesAsync(
        IAppDbContext db,
        IReadOnlyList<ProjectMilestone> milestones,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        if (milestones.Count == 0)
        {
            return [];
        }

        var ids = milestones.Select(m => m.Id).ToList();
        var progress = await ProgressAsync(db.WorkItems.Where(w => w.MilestoneId != null && ids.Contains(w.MilestoneId.Value)), w => w.MilestoneId!.Value, today, cancellationToken)
            .ConfigureAwait(false);
        return
        [
            .. milestones.Select(m => new ProjectMilestoneDto(
                m.Id,
                m.ProjectId,
                m.Name,
                m.Description,
                m.DueDate,
                !m.IsCompleted && m.DueDate < today,
                m.CompletedAt,
                progress.GetValueOrDefault(m.Id, NoTasks),
                m.UpdatedAt)),
        ];
    }

    public static async Task<IReadOnlyList<ProjectMemberDto>> MembersAsync(IAppDbContext db, Guid projectId, IReadOnlyList<ProjectMember> members, CancellationToken cancellationToken)
    {
        if (members.Count == 0)
        {
            return [];
        }

        var open = await db.WorkItems.AsNoTracking()
            .Where(w => w.ProjectId == projectId && w.Status != WorkItemStatus.Done && w.Status != WorkItemStatus.Cancelled
                        && (w.AssigneeUserId != null || w.AssigneeAgentId != null))
            .GroupBy(w => new { w.AssigneeUserId, w.AssigneeAgentId })
            .Select(g => new { g.Key.AssigneeUserId, g.Key.AssigneeAgentId, Count = g.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var actors = await ActorNames.ResolveAsync(db, members.Select(MemberOf), cancellationToken).ConfigureAwait(false);
        return
        [
            .. members.Select(m => new ProjectMemberDto(
                m.Id,
                actors(MemberOf(m)),
                m.Responsibility,
                open.Where(o => (m.UserId != null && o.AssigneeUserId == m.UserId) || (m.AgentId != null && o.AssigneeAgentId == m.AgentId)).Sum(o => o.Count),
                m.CreatedAt)),
        ];
    }

    public static Actor? LeadOf(Project project)
        => project.LeadUserId is { } user ? Actor.ForUser(user) : project.LeadAgentId is { } agent ? Actor.ForAgent(agent) : null;

    public static Actor MemberOf(ProjectMember member) => member.UserId is { } user ? Actor.ForUser(user) : Actor.ForAgent(member.AgentId!.Value);

    /// <summary>
    /// 按项目或里程碑分组统计任务。已取消的任务不计入；逾期按"今天"判断。
    /// </summary>
    private static async Task<Dictionary<Guid, ProjectProgressDto>> ProgressAsync(
        IQueryable<WorkItem> tasks,
        Expression<Func<WorkItem, Guid>> key,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var rows = await tasks.AsNoTracking().Where(w => w.Status != WorkItemStatus.Cancelled)
            .GroupBy(key)
            .Select(g => new
            {
                g.Key,
                Total = g.Count(),
                Done = g.Count(w => w.Status == WorkItemStatus.Done),
                InProgress = g.Count(w => w.Status == WorkItemStatus.InProgress || w.Status == WorkItemStatus.InReview),
                Blocked = g.Count(w => w.Status == WorkItemStatus.Blocked),
                Overdue = g.Count(w => w.Status != WorkItemStatus.Done && w.DueDate != null && w.DueDate < today),
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.ToDictionary(
            r => r.Key,
            r => new ProjectProgressDto(r.Total, r.Done, r.InProgress, r.Blocked, r.Overdue, r.Total == 0 ? 0 : r.Done * 100 / r.Total));
    }
}

/// <summary>
/// 项目管理各处理器共用的查找、校验与写入辅助。
/// </summary>
internal sealed class ProjectRecords(IAppDbContext db, ITenantContext tenant, IPlatformEvents events, ITransactionLock locks, TenantCalendar calendar)
{
    /// <summary>
    /// 往项目或里程碑里加任务、完成项目、完成里程碑都在这把锁下进行：
    /// "还有没有未关闭的任务"的检查不会和加入新任务交错。
    /// </summary>
    public static string LockKey(Guid projectId) => $"project:{projectId}";

    /// <summary>空间时区下的今天，逾期按它判断。</summary>
    public Task<DateOnly> TodayAsync(CancellationToken cancellationToken) => calendar.TodayAsync(cancellationToken);

    public Task LockAsync(Guid projectId, CancellationToken cancellationToken) => locks.AcquireAsync(LockKey(projectId), cancellationToken);

    public async Task<Project> ProjectAsync(Guid id, CancellationToken cancellationToken)
        => await db.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken).ConfigureAwait(false)
           ?? throw new NotFoundException("project", id);

    public async Task<ProjectMilestone> MilestoneAsync(Guid projectId, Guid milestoneId, CancellationToken cancellationToken)
        => await db.ProjectMilestones.FirstOrDefaultAsync(m => m.Id == milestoneId && m.ProjectId == projectId, cancellationToken).ConfigureAwait(false)
           ?? throw new NotFoundException("project_milestone", milestoneId);

    /// <summary>
    /// 项目名称在空间内唯一（不区分大小写）。在空间级的事务锁下检查，并发新建同名项目只有一个成功。
    /// </summary>
    public async Task EnsureNameAvailableAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        await locks.AcquireAsync($"project-names:{tenant.TenantId}", cancellationToken).ConfigureAwait(false);
        var lower = name.ToLowerInvariant();
        if (await db.Projects.AnyAsync(p => p.Name.ToLower() == lower && p.Id != exceptId, cancellationToken).ConfigureAwait(false))
        {
            throw new ConflictException("project.duplicate_name", $"已经有名为「{name}」的项目了。");
        }
    }

    public async Task EnsureUserAsync(Guid? userId, CancellationToken cancellationToken)
    {
        if (userId is { } id && !await db.Memberships.AnyAsync(m => m.UserId == id, cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException("member", id);
        }
    }

    public async Task EnsureAgentAsync(Guid? agentId, CancellationToken cancellationToken)
    {
        if (agentId is not { } id)
        {
            return;
        }

        var status = await db.Agents.Where(a => a.Id == id).Select(a => (AgentStatus?)a.Status).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                     ?? throw new NotFoundException("agent", id);
        if (status == AgentStatus.Terminated)
        {
            throw new ConflictException("agent.terminated", "已终止的智能体不能负责或加入项目。");
        }
    }

    public async Task<ProjectDto> ToDtoAsync(Project project, CancellationToken cancellationToken)
        => (await ProjectMapping.ProjectsAsync(db, [project], await TodayAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false))[0];

    public async Task<ProjectMilestoneDto> ToDtoAsync(ProjectMilestone milestone, CancellationToken cancellationToken)
        => (await ProjectMapping.MilestonesAsync(db, [milestone], await TodayAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false))[0];

    public async Task<ProjectDetailDto> DetailAsync(Project project, CancellationToken cancellationToken)
    {
        var milestones = await db.ProjectMilestones.AsNoTracking().Where(m => m.ProjectId == project.Id)
            .OrderBy(m => m.DueDate == null).ThenBy(m => m.DueDate).ThenBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var members = await db.ProjectMembers.AsNoTracking().Where(m => m.ProjectId == project.Id)
            .OrderBy(m => m.UserId == null).ThenBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return await DetailAsync(project, milestones, members, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectDetailDto> DetailAsync(Project project, IReadOnlyList<ProjectMilestone> milestones, IReadOnlyList<ProjectMember> members, CancellationToken cancellationToken)
        => new(
            await ToDtoAsync(project, cancellationToken).ConfigureAwait(false),
            await ProjectMapping.MilestonesAsync(db, milestones, await TodayAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false),
            await ProjectMapping.MembersAsync(db, project.Id, members, cancellationToken).ConfigureAwait(false));

    public static ProjectMilestone NewMilestone(Guid projectId, SaveProjectMilestoneRequest request)
    {
        var milestone = new ProjectMilestone { ProjectId = projectId, Name = request.Name.Trim() };
        Apply(milestone, request);
        return milestone;
    }

    public static void Apply(ProjectMilestone milestone, SaveProjectMilestoneRequest request)
    {
        milestone.Name = request.Name.Trim();
        milestone.Description = request.Description.Trim();
        milestone.DueDate = request.DueDate;
    }

    /// <summary>
    /// 发布项目事件（订阅的工作流随本事务创建运行）。载荷只在有订阅者时构造，自动带上操作者。
    /// </summary>
    public Task PublishAsync(string eventType, Guid subjectId, Func<Task<Dictionary<string, object?>>> payload, CancellationToken cancellationToken)
        => events.PublishAsync(eventType, subjectId, async () =>
        {
            var data = await payload().ConfigureAwait(false);
            var actors = await ActorNames.ResolveAsync(db, [tenant.Actor], cancellationToken).ConfigureAwait(false);
            data["actor"] = actors(tenant.Actor);
            return data;
        }, cancellationToken);

    public static (int Page, int PageSize) Paging(int page, int pageSize) => Common.Paging.Normalize(page, pageSize);
}

/// <summary>
/// 任务归入项目和里程碑时的校验。任务接口（人、智能体、工作流）都经过这里。
/// </summary>
internal sealed class ProjectPlacement(IAppDbContext db, IModuleAccess modules, ITransactionLock locks)
{
    /// <summary>
    /// 算出任务最终的项目和里程碑：只给里程碑时取它所在的项目。归属没变时不做检查，已结束项目里的旧任务照常可以编辑；
    /// 移出项目总是允许；加入时项目必须进行中、里程碑必须属于该项目且未完成。
    /// </summary>
    public async Task<(Guid? ProjectId, Guid? MilestoneId)> ResolveAsync(
        Guid? projectId,
        Guid? milestoneId,
        Guid? currentProjectId,
        Guid? currentMilestoneId,
        CancellationToken cancellationToken)
    {
        if (milestoneId is { } onlyMilestone && projectId is null)
        {
            projectId = await db.ProjectMilestones.Where(m => m.Id == onlyMilestone).Select(m => (Guid?)m.ProjectId)
                            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                        ?? throw new NotFoundException("project_milestone", onlyMilestone);
        }

        if (projectId is null)
        {
            return (null, null);
        }

        // 归属没变，或只是在原项目里移出里程碑。
        if (projectId == currentProjectId && (milestoneId == currentMilestoneId || milestoneId is null))
        {
            return (projectId, milestoneId);
        }

        var target = projectId.Value;
        if (!await modules.IsEnabledAsync(ProjectsModule.ModuleKey, cancellationToken).ConfigureAwait(false))
        {
            throw new ForbiddenException("项目管理模块未启用，不能把任务放进项目。");
        }

        await locks.AcquireAsync(ProjectRecords.LockKey(target), cancellationToken).ConfigureAwait(false);
        var project = await db.Projects.AsNoTracking().Where(p => p.Id == target).Select(p => new { p.Name, p.Status })
                          .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("project", target);
        if (project.Status is ProjectStatus.Completed or ProjectStatus.Cancelled)
        {
            throw new ConflictException("project.closed", $"项目「{project.Name}」已经结束，不能再加入任务。需要的话先把项目改回进行中。");
        }

        if (milestoneId is { } id && id != currentMilestoneId)
        {
            var milestone = await db.ProjectMilestones.AsNoTracking().Where(m => m.Id == id).Select(m => new { m.ProjectId, m.Name, m.CompletedAt })
                                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                            ?? throw new NotFoundException("project_milestone", id);
            if (milestone.ProjectId != target)
            {
                throw new ValidationFailedException(new Dictionary<string, string[]> { ["milestoneId"] = ["里程碑不属于所选项目。"] });
            }

            if (milestone.CompletedAt is not null)
            {
                throw new ConflictException("project_milestone.completed", $"里程碑「{milestone.Name}」已经完成，不能再加入任务。需要的话先重新打开它。");
            }
        }

        return (target, milestoneId);
    }
}

/// <summary>
/// 项目负责人是智能体时的协作：被设为负责人、项目里的任务被阻塞、里程碑或整个项目的任务全部关闭时唤醒它。
/// 自己造成的变化不唤醒自己。
/// </summary>
internal sealed class ProjectWorkflow(IAppDbContext db, IWakeupQueue wakeups, ITransactionLock locks, TimeProvider clock)
{
    public Task OnLeadAssignedAsync(Project project, Guid? previousLeadAgentId, Actor actor, CancellationToken cancellationToken)
    {
        if (project.LeadAgentId is not { } agentId || agentId == previousLeadAgentId || IsSelf(actor, agentId) || project.IsClosed)
        {
            return Task.CompletedTask;
        }

        return WakeAsync(
            agentId,
            WakeupSource.Assignment,
            $"你被设为项目《{project.Name}》的负责人（项目 id {project.Id}）。请查看项目目标，规划里程碑和任务并安排给合适的同事。",
            null,
            actor,
            cancellationToken);
    }

    /// <param name="item">状态已经改好、尚未保存的任务。</param>
    public async Task OnTaskStatusChangedAsync(WorkItem item, WorkItemStatus previous, Actor actor, CancellationToken cancellationToken)
    {
        if (previous == item.Status || item.ProjectId is not { } projectId)
        {
            return;
        }

        // 在项目锁下统计：并发关闭最后两个任务时，后提交的一方能看到先提交的关闭，不会谁都不通知负责人；
        // 也不会和"完成项目"前的未关闭任务检查交错。
        await locks.AcquireAsync(ProjectRecords.LockKey(projectId), cancellationToken).ConfigureAwait(false);
        var project = await db.Projects.AsNoTracking().Where(p => p.Id == projectId)
            .Select(p => new { p.Id, p.Name, p.LeadAgentId, p.Status })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (project?.LeadAgentId is not { } leadId || IsSelf(actor, leadId) || project.Status is ProjectStatus.Completed or ProjectStatus.Cancelled)
        {
            return;
        }

        var task = $"T-{item.Number}《{item.Title}》";
        if (item.Status == WorkItemStatus.Blocked)
        {
            await WakeAsync(leadId, WakeupSource.Delegation, $"项目《{project.Name}》的任务 {task} 被阻塞了，需要你协调（项目 id {project.Id}）。", item.Id, actor, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!item.IsClosed)
        {
            return;
        }

        // 这个任务的新状态还没保存，统计其他任务时把它排除在外。
        var openTasks = db.WorkItems.Where(w => w.ProjectId == projectId && w.Id != item.Id && w.Status != WorkItemStatus.Done && w.Status != WorkItemStatus.Cancelled);
        if (item.MilestoneId is { } milestoneId && !await openTasks.AnyAsync(w => w.MilestoneId == milestoneId, cancellationToken).ConfigureAwait(false))
        {
            var milestone = await db.ProjectMilestones.AsNoTracking().Where(m => m.Id == milestoneId).Select(m => new { m.Name, m.CompletedAt })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (milestone is { CompletedAt: null })
            {
                await WakeAsync(
                    leadId,
                    WakeupSource.Delegation,
                    $"项目《{project.Name}》的里程碑「{milestone.Name}」下的任务都已关闭（最后一个是 {task}），请验收并标记里程碑完成（项目 id {project.Id}，里程碑 id {milestoneId}）。",
                    null,
                    actor,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (!await openTasks.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            await WakeAsync(
                leadId,
                WakeupSource.Delegation,
                $"项目《{project.Name}》的任务都已关闭（最后一个是 {task}），请验收结果，决定是否完成项目（项目 id {project.Id}）。",
                null,
                actor,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private Task WakeAsync(Guid agentId, WakeupSource source, string reason, Guid? workItemId, Actor actor, CancellationToken cancellationToken)
        => wakeups.EnqueueAsync(
            agentId,
            new WakeupItem { Source = source, Reason = reason, WorkItemId = workItemId, RequestedAt = clock.GetUtcNow() },
            actor,
            cancellationToken: cancellationToken);

    private static bool IsSelf(Actor actor, Guid agentId) => actor.IsAgent && actor.Id == agentId;
}
