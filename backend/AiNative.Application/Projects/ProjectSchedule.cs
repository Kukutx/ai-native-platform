using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Common;
using AiNative.Application.Work;
using AiNative.Contracts.Projects;
using AiNative.Domain.Common;
using AiNative.Domain.Work;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Projects;

/// <summary>
/// 项目排期（甘特图）：任务的起止日期、项目内的依赖、排期冲突和关键路径。要同时能看项目和任务。
/// </summary>
[RequireFeature(ProjectFeatures.View)]
[RequireFeature(Features.Tasks.View)]
public sealed record GetProjectScheduleQuery(Guid ProjectId) : IQuery<ProjectScheduleDto>;

internal sealed class ProjectScheduleHandler(IAppDbContext db, ProjectRecords records) : IRequestHandler<GetProjectScheduleQuery, ProjectScheduleDto>
{
    /// <summary>
    /// 一次最多排这么多任务；更多时优先保留未关闭、截止日期早的。
    /// </summary>
    public const int MaxTasks = 500;

    public async Task<ProjectScheduleDto> HandleAsync(GetProjectScheduleQuery request, CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.ProjectId, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("project", request.ProjectId);
        var detail = await records.DetailAsync(project, cancellationToken).ConfigureAwait(false);
        var rows = await db.WorkItems.AsNoTracking()
            .Where(w => w.ProjectId == project.Id && w.Status != WorkItemStatus.Cancelled)
            .OrderBy(w => w.Status == WorkItemStatus.Done).ThenBy(w => w.DueDate == null).ThenBy(w => w.DueDate).ThenBy(w => w.Number)
            .Take(MaxTasks + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var truncated = rows.Count > MaxTasks;
        var milestoneOrder = detail.Milestones.Select((m, index) => (m.Id, index)).ToDictionary(x => x.Id, x => x.index);
        var nodes = rows.Take(MaxTasks).ToDictionary(w => w.Id, w => new ScheduleNode(w.Id, w.StartDate, w.DueDate, w.IsClosed));
        var tasks = rows.Take(MaxTasks)
            .OrderBy(w => w.MilestoneId is { } m && milestoneOrder.TryGetValue(m, out var index) ? index : int.MaxValue)
            .ThenBy(w => nodes[w.Id].Span is null).ThenBy(w => nodes[w.Id].Span?.Start).ThenBy(w => w.Number)
            .ToList();

        var ids = nodes.Keys.ToList();
        var edges = await db.WorkItemDependencies.AsNoTracking()
            .Where(d => ids.Contains(d.WorkItemId) && ids.Contains(d.DependsOnId))
            .Select(d => new ScheduleEdge(d.WorkItemId, d.DependsOnId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // 还没关闭的前置任务数，包括其他项目里的前置任务。
        var waiting = await db.WorkItemDependencies.AsNoTracking()
            .Where(d => ids.Contains(d.WorkItemId)
                        && db.WorkItems.Any(p => p.Id == d.DependsOnId && p.Status != WorkItemStatus.Done && p.Status != WorkItemStatus.Cancelled))
            .GroupBy(d => d.WorkItemId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken).ConfigureAwait(false);
        var critical = ScheduleCalculator.CriticalPath(nodes.Values, edges);
        var actors = await ActorNames.ResolveAsync(db, tasks.Select(AssigneeOf).Where(a => a.HasValue).Select(a => a!.Value), cancellationToken)
            .ConfigureAwait(false);
        var links = edges.Select(e => new ScheduleLinkDto(e.Predecessor, e.Successor, ScheduleCalculator.IsConflict(nodes[e.Predecessor], nodes[e.Successor])))
            .ToList();

        var today = await records.TodayAsync(cancellationToken).ConfigureAwait(false);
        var dates = nodes.Values.SelectMany(n => n.Span is { } span ? new[] { span.Start, span.End } : [])
            .Concat(detail.Milestones.Where(m => m.DueDate.HasValue).Select(m => m.DueDate!.Value))
            .Concat(new[] { project.StartDate, project.TargetDate }.Where(d => d.HasValue).Select(d => d!.Value))
            .ToList();
        return new ProjectScheduleDto(
            detail.Project,
            detail.Milestones,
            [
                .. tasks.Select(w => new ScheduledTaskDto(
                    w.Id,
                    WorkItemMapping.KeyOf(w.Number),
                    w.Title,
                    w.Status,
                    w.Priority,
                    w.MilestoneId,
                    w.ParentId,
                    AssigneeOf(w) is { } assignee ? actors(assignee) : null,
                    w.StartDate,
                    w.DueDate,
                    w.CompletedAt,
                    !w.IsClosed && w.DueDate < today,
                    waiting.GetValueOrDefault(w.Id),
                    critical.Contains(w.Id),
                    w.UpdatedAt)),
            ],
            links,
            dates.Count == 0 ? null : dates.Min(),
            dates.Count == 0 ? null : dates.Max(),
            links.Count(l => l.Conflict),
            nodes.Values.Count(n => n.Span is null),
            truncated);
    }

    private static Actor? AssigneeOf(WorkItem item)
        => item.AssigneeUserId is { } user ? Actor.ForUser(user) : item.AssigneeAgentId is { } agent ? Actor.ForAgent(agent) : null;
}
