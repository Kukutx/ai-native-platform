using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Common;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Work;
using AiNative.Domain.Work;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Work;

/// <summary>
/// 让 <paramref name="WorkItemId"/> 等 <paramref name="DependsOnId"/> 关闭后才能开始。返回前置任务的摘要。
/// </summary>
[RequireFeature(Features.Tasks.Manage)]
public sealed record AddWorkItemDependencyCommand(Guid WorkItemId, Guid DependsOnId) : ICommand<WorkItemLinkDto>;

/// <summary>
/// 去掉一条依赖。后续任务因此不再等任何前置任务时，唤醒它的负责智能体。返回被去掉的前置任务的摘要。
/// </summary>
[RequireFeature(Features.Tasks.Manage)]
public sealed record RemoveWorkItemDependencyCommand(Guid WorkItemId, Guid DependsOnId) : ICommand<WorkItemLinkDto>;

/// <summary>
/// 调整任务的开始和截止日期，可选顺延后续任务。甘特图拖动、智能体排期都走这里。
/// </summary>
[RequireFeature(Features.Tasks.Manage)]
public sealed record ScheduleWorkItemCommand(Guid Id, ScheduleWorkItemRequest Request) : ICommand<ScheduleWorkItemResultDto>;

public sealed class AddWorkItemDependencyCommandValidator : AbstractValidator<AddWorkItemDependencyCommand>
{
    public AddWorkItemDependencyCommandValidator()
        => RuleFor(x => x.DependsOnId).NotEqual(x => x.WorkItemId).WithMessage("任务不能依赖自己。").OverridePropertyName("dependsOnId");
}

public sealed class ScheduleWorkItemCommandValidator : AbstractValidator<ScheduleWorkItemCommand>
{
    public ScheduleWorkItemCommandValidator()
        => RuleFor(x => x.Request.StartDate).Must((x, start) => start is null || x.Request.DueDate is null || start <= x.Request.DueDate)
            .WithMessage("开始日期不能晚于截止日期。").OverridePropertyName("startDate");
}

/// <summary>
/// 依赖关系的校验与登记。加依赖（检查循环）和顺延后续任务（沿依赖链读写）都在空间级的锁下进行：
/// 同时加 A→B 和 B→A 时后一个会看到前一个，不会形成循环；顺延期间依赖链不会变化。
/// </summary>
internal sealed class WorkItemDependencyRules(IAppDbContext db, ITenantContext tenant, IWorkItemGraph graph, ITransactionLock locks)
{
    public const int MaxPredecessors = 50;

    public Task LockAsync(CancellationToken cancellationToken) => locks.AcquireAsync($"task-dependencies:{tenant.TenantId}", cancellationToken);

    /// <summary>
    /// 新任务的前置任务：新任务还没有后续任务，不会形成循环，只需确认前置任务都存在。
    /// </summary>
    public async Task AddToNewAsync(WorkItem item, IReadOnlyCollection<Guid> dependsOn, CancellationToken cancellationToken)
    {
        var ids = dependsOn.Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var found = await db.WorkItems.Where(w => ids.Contains(w.Id)).Select(w => w.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var missing = ids.Except(found).ToList();
        if (missing.Count > 0)
        {
            throw new NotFoundException("work_item", missing[0]);
        }

        db.WorkItemDependencies.AddRange(ids.Select(id => new WorkItemDependency { WorkItemId = item.Id, DependsOnId = id }));
    }

    public async Task AddAsync(WorkItem item, WorkItem predecessor, CancellationToken cancellationToken)
    {
        await LockAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.WorkItemDependencies.Where(d => d.WorkItemId == item.Id).Select(d => d.DependsOnId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Contains(predecessor.Id))
        {
            throw new ConflictException("work_item_dependency.duplicate", $"{Key(item)} 已经在等 {Key(predecessor)} 了。");
        }

        if (existing.Count >= MaxPredecessors)
        {
            throw new ConflictException("work_item_dependency.too_many", $"一个任务最多 {MaxPredecessors} 个前置任务。");
        }

        if (await graph.DependsOnAsync(predecessor.Id, item.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new ConflictException(
                "work_item_dependency.cycle",
                $"{Key(predecessor)} 已经直接或间接依赖 {Key(item)}，再让 {Key(item)} 等 {Key(predecessor)} 会形成循环。");
        }

        db.WorkItemDependencies.Add(new WorkItemDependency { WorkItemId = item.Id, DependsOnId = predecessor.Id });
    }

    private static string Key(WorkItem item) => WorkItemMapping.KeyOf(item.Number);
}

internal sealed class WorkItemDependencyHandlers(
    IAppDbContext db,
    ITenantContext tenant,
    WorkItemDependencyRules rules,
    WorkItemWorkflow workflow,
    IWorkItemGraph graph,
    IChangeFeed changes,
    TimeProvider clock) :
    IRequestHandler<AddWorkItemDependencyCommand, WorkItemLinkDto>,
    IRequestHandler<RemoveWorkItemDependencyCommand, WorkItemLinkDto>,
    IRequestHandler<ScheduleWorkItemCommand, ScheduleWorkItemResultDto>
{
    public async Task<WorkItemLinkDto> HandleAsync(AddWorkItemDependencyCommand request, CancellationToken cancellationToken)
    {
        var item = await FindAsync(request.WorkItemId, cancellationToken).ConfigureAwait(false);
        var predecessor = await FindAsync(request.DependsOnId, cancellationToken).ConfigureAwait(false);
        await rules.AddAsync(item, predecessor, cancellationToken).ConfigureAwait(false);

        // 依赖不在任务的编辑表单里，不推进 UpdatedAt，只通知两端刷新。
        changes.Publish(RealtimeEntities.WorkItem, item.Id, RealtimeChangeKind.Updated);
        changes.Publish(RealtimeEntities.WorkItem, predecessor.Id, RealtimeChangeKind.Updated);
        return predecessor.ToLink();
    }

    public async Task<WorkItemLinkDto> HandleAsync(RemoveWorkItemDependencyCommand request, CancellationToken cancellationToken)
    {
        var item = await FindAsync(request.WorkItemId, cancellationToken).ConfigureAwait(false);
        var predecessor = await FindAsync(request.DependsOnId, cancellationToken).ConfigureAwait(false);
        var removed = await db.WorkItemDependencies.Where(d => d.WorkItemId == item.Id && d.DependsOnId == predecessor.Id)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (removed == 0)
        {
            throw new NotFoundException("work_item_dependency", predecessor.Id);
        }

        await workflow.OnDependencyRemovedAsync(item, predecessor, tenant.Actor, cancellationToken).ConfigureAwait(false);
        changes.Publish(RealtimeEntities.WorkItem, item.Id, RealtimeChangeKind.Updated);
        changes.Publish(RealtimeEntities.WorkItem, predecessor.Id, RealtimeChangeKind.Updated);
        return predecessor.ToLink();
    }

    public async Task<ScheduleWorkItemResultDto> HandleAsync(ScheduleWorkItemCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        var item = await FindAsync(request.Id, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (r.ExpectedUpdatedAt is { } expected)
        {
            await OptimisticConcurrency.EnsureUnchangedAsync(
                db.WorkItems, item.Id, expected, now, "work_item.modified", "任务刚被其他人或智能体修改过，已刷新为最新内容，请确认后再改。", cancellationToken)
                .ConfigureAwait(false);
        }

        item.Reschedule(r.StartDate, r.DueDate);
        item.UpdatedAt = now;
        changes.Publish(RealtimeEntities.WorkItem, item.Id, RealtimeChangeKind.Updated);
        var shifted = r.ShiftDependents ? await ShiftDependentsAsync(item, now, cancellationToken).ConfigureAwait(false) : [];
        var actors = await ActorNames.ResolveAsync(db, [item.CreatedBy], cancellationToken).ConfigureAwait(false);
        return new ScheduleWorkItemResultDto(item.ToDto(actors), shifted);
    }

    /// <summary>
    /// 在依赖锁下读出下游任务，按 <see cref="ScheduleCalculator.PlanShift"/> 算出要挪的任务，逐个条件更新：
    /// 读到之后日期被别人改过就整体回滚并返回冲突，不会覆盖别人的修改。
    /// </summary>
    private async Task<IReadOnlyList<ShiftedWorkItemDto>> ShiftDependentsAsync(WorkItem root, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await rules.LockAsync(cancellationToken).ConfigureAwait(false);
        var edges = await graph.DownstreamAsync(root.Id, cancellationToken).ConfigureAwait(false);
        if (edges.Count == 0)
        {
            return [];
        }

        var ids = edges.Select(e => e.Successor).Distinct().ToList();
        var tasks = await db.WorkItems.AsNoTracking().Where(w => ids.Contains(w.Id))
            .Select(w => new { w.Id, w.Number, w.Title, w.StartDate, w.DueDate, w.Status })
            .ToDictionaryAsync(w => w.Id, cancellationToken).ConfigureAwait(false);
        var nodes = tasks.Values.ToDictionary(
            t => t.Id,
            t => new ScheduleNode(t.Id, t.StartDate, t.DueDate, t.Status is WorkItemStatus.Done or WorkItemStatus.Cancelled));
        var plan = ScheduleCalculator.PlanShift(new ScheduleNode(root.Id, root.StartDate, root.DueDate, root.IsClosed), nodes, edges);
        var shifted = new List<ShiftedWorkItemDto>(plan.Count);
        foreach (var shift in plan)
        {
            var task = tasks[shift.Id];
            var updated = await db.WorkItems
                .Where(w => w.Id == shift.Id && w.StartDate == task.StartDate && w.DueDate == task.DueDate)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(w => w.StartDate, shift.Start).SetProperty(w => w.DueDate, shift.Due).SetProperty(w => w.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);
            var key = WorkItemMapping.KeyOf(task.Number);
            if (updated == 0)
            {
                throw new ConflictException("work_item.modified", $"后续任务 {key} 刚被修改过，请刷新后重试。");
            }

            changes.Publish(RealtimeEntities.WorkItem, shift.Id, RealtimeChangeKind.Updated);
            shifted.Add(new ShiftedWorkItemDto(shift.Id, key, task.Title, shift.Start, shift.Due, shift.Days));
        }

        return shifted;
    }

    private async Task<WorkItem> FindAsync(Guid id, CancellationToken cancellationToken)
        => await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, cancellationToken).ConfigureAwait(false)
           ?? throw new NotFoundException("work_item", id);
}
