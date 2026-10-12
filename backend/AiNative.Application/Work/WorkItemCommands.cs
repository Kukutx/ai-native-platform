using System.Text.RegularExpressions;
using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Common;
using AiNative.Application.Modules;
using AiNative.Application.Projects;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Common;
using AiNative.Contracts.Work;
using AiNative.Domain.Common;
using AiNative.Domain.Work;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Work;

[RequireFeature(Features.Tasks.Manage)]
public sealed record CreateWorkItemCommand(CreateWorkItemRequest Request) : ICommand<WorkItemDto>;

[RequireFeature(Features.Tasks.Manage)]
public sealed record UpdateWorkItemCommand(Guid Id, UpdateWorkItemRequest Request) : ICommand<WorkItemDto>;

[RequireFeature(Features.Tasks.Manage)]
public sealed record ChangeWorkItemStatusCommand(Guid Id, WorkItemStatus Status, string? Comment = null, Guid? RunId = null) : ICommand<WorkItemDto>;

[RequireFeature(Features.Tasks.Manage)]
public sealed record AssignWorkItemCommand(Guid Id, Guid? AgentId, Guid? UserId) : ICommand<WorkItemDto>;

[RequireFeature(Features.Tasks.Comment)]
public sealed record AddCommentCommand(Guid WorkItemId, string Body, Guid? RunId = null) : ICommand<CommentDto>;

[RequireFeature(Features.Tasks.View)]
public sealed record ListWorkItemsQuery(
    WorkItemStatus? Status = null,
    Guid? AssigneeAgentId = null,
    Guid? ParentId = null,
    string? Search = null,
    bool OpenOnly = false,
    int Page = 1,
    int PageSize = 50,
    Guid? ProjectId = null,
    Guid? MilestoneId = null,
    WorkItemPriority? Priority = null,
    Guid? AssigneeUserId = null) : IQuery<PagedResult<WorkItemDto>>;

[RequireFeature(Features.Tasks.View)]
public sealed record GetWorkItemQuery(Guid Id) : IQuery<WorkItemDetailDto>;

public sealed class CreateWorkItemCommandValidator : AbstractValidator<CreateWorkItemCommand>
{
    public CreateWorkItemCommandValidator()
    {
        RuleFor(x => x.Request.Title).NotEmpty().MaximumLength(300);
        RuleFor(x => x.Request.Description).MaximumLength(50_000);
        RuleFor(x => x.Request).Must(r => r.AssigneeAgentId is null || r.AssigneeUserId is null)
            .WithMessage("任务只能指派给一个负责人。");
        RuleFor(x => x.Request.StartDate).Must((x, start) => start is null || x.Request.DueDate is null || start <= x.Request.DueDate)
            .WithMessage("开始日期不能晚于截止日期。").OverridePropertyName("startDate");
        RuleFor(x => x.Request.DependsOn).Must(d => d is null || d.Distinct().Count() <= WorkItemDependencyRules.MaxPredecessors)
            .WithMessage($"一个任务最多 {WorkItemDependencyRules.MaxPredecessors} 个前置任务。").OverridePropertyName("dependsOn");
    }
}

public sealed class UpdateWorkItemCommandValidator : AbstractValidator<UpdateWorkItemCommand>
{
    public UpdateWorkItemCommandValidator()
    {
        RuleFor(x => x.Request.Title).NotEmpty().MaximumLength(300);
        RuleFor(x => x.Request.Description).MaximumLength(50_000);
        RuleFor(x => x.Request).Must(r => r.AssigneeAgentId is null || r.AssigneeUserId is null)
            .WithMessage("任务只能指派给一个负责人。");
        RuleFor(x => x.Request.StartDate).Must((x, start) => start is null || x.Request.DueDate is null || start <= x.Request.DueDate)
            .WithMessage("开始日期不能晚于截止日期。").OverridePropertyName("startDate");
    }
}

public sealed class AddCommentCommandValidator : AbstractValidator<AddCommentCommand>
{
    public AddCommentCommandValidator() => RuleFor(x => x.Body).NotEmpty().MaximumLength(20_000);
}

public sealed class ChangeWorkItemStatusCommandValidator : AbstractValidator<ChangeWorkItemStatusCommand>
{
    public ChangeWorkItemStatusCommandValidator()
    {
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.Comment).MaximumLength(20_000);
    }
}

internal sealed class WorkItemCommandHandlers(
    IAppDbContext db,
    ITenantContext tenant,
    IWorkItemNumbers numbers,
    WorkItemWorkflow workflow,
    WorkItemDependencyRules dependencies,
    ProjectPlacement placement,
    IChangeFeed changes,
    IPlatformEvents events,
    TimeProvider clock) :
    IRequestHandler<CreateWorkItemCommand, WorkItemDto>,
    IRequestHandler<UpdateWorkItemCommand, WorkItemDto>,
    IRequestHandler<ChangeWorkItemStatusCommand, WorkItemDto>,
    IRequestHandler<AssignWorkItemCommand, WorkItemDto>,
    IRequestHandler<AddCommentCommand, CommentDto>
{
    public async Task<WorkItemDto> HandleAsync(CreateWorkItemCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        var (projectId, milestoneId) = (r.ProjectId, r.MilestoneId);
        if (r.ParentId is { } parentId)
        {
            var parent = await db.WorkItems.Where(w => w.Id == parentId).Select(w => new { w.ProjectId, w.MilestoneId })
                             .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                         ?? throw new NotFoundException("work_item", parentId);

            // 子任务默认跟上级任务在同一个项目和里程碑里。
            if (projectId is null && milestoneId is null)
            {
                (projectId, milestoneId) = (parent.ProjectId, parent.MilestoneId);
            }
        }

        await EnsureUserIsMemberAsync(r.AssigneeUserId, cancellationToken).ConfigureAwait(false);
        var placed = await placement.ResolveAsync(projectId, milestoneId, null, null, cancellationToken).ConfigureAwait(false);

        var item = new WorkItem
        {
            Number = await numbers.NextAsync(cancellationToken).ConfigureAwait(false),
            Title = r.Title.Trim(),
            Description = r.Description.Trim(),
            Priority = r.Priority,
            ParentId = r.ParentId,
            CreatedBy = tenant.Actor,
        };
        item.PlaceIn(placed.ProjectId, placed.MilestoneId);
        item.Reschedule(r.StartDate, r.DueDate);
        item.ChangeStatus(r.Status, clock.GetUtcNow());
        item.AssignToAgent(r.AssigneeAgentId);
        if (r.AssigneeUserId is not null)
        {
            item.AssignToUser(r.AssigneeUserId);
        }

        db.WorkItems.Add(item);

        // 先登记前置任务，指派的智能体在前置任务关闭前不会被唤醒。
        await dependencies.AddToNewAsync(item, r.DependsOn ?? [], cancellationToken).ConfigureAwait(false);
        await workflow.OnAssignedAsync(item, null, tenant.Actor, cancellationToken).ConfigureAwait(false);
        changes.Publish(RealtimeEntities.WorkItem, item.Id, RealtimeChangeKind.Created);
        var dto = await ToDtoAsync(item, cancellationToken).ConfigureAwait(false);
        await PublishAsync(PlatformEventKeys.TaskCreated, item, dto, cancellationToken).ConfigureAwait(false);
        return dto;
    }

    public async Task<WorkItemDto> HandleAsync(UpdateWorkItemCommand request, CancellationToken cancellationToken)
    {
        var item = await FindAsync(request.Id, cancellationToken).ConfigureAwait(false);
        var r = request.Request;
        var now = clock.GetUtcNow();
        if (r.Status is WorkItemStatus.Done or WorkItemStatus.Cancelled && !item.IsClosed)
        {
            // 关闭任务要拿依赖锁（见 WorkItemWorkflow.OnStatusChangedAsync），在放进项目拿项目锁之前先拿，保持锁的顺序一致。
            await dependencies.LockAsync(cancellationToken).ConfigureAwait(false);
        }

        if (r.ExpectedUpdatedAt is { } expected)
        {
            await OptimisticConcurrency.EnsureUnchangedAsync(
                db.WorkItems, item.Id, expected, now, "work_item.modified", "任务刚被其他人或智能体修改过，已刷新为最新内容，请确认后再改。", cancellationToken)
                .ConfigureAwait(false);
        }

        var previousAgent = item.AssigneeAgentId;
        var previousStatus = item.Status;
        await EnsureUserIsMemberAsync(r.AssigneeUserId, cancellationToken).ConfigureAwait(false);
        var placed = await placement.ResolveAsync(r.ProjectId, r.MilestoneId, item.ProjectId, item.MilestoneId, cancellationToken).ConfigureAwait(false);

        item.Title = r.Title.Trim();
        item.Description = r.Description.Trim();
        item.Priority = r.Priority;
        item.PlaceIn(placed.ProjectId, placed.MilestoneId);
        item.Reschedule(r.StartDate, r.DueDate);
        item.ChangeStatus(r.Status, clock.GetUtcNow());
        item.AssignToAgent(r.AssigneeAgentId);
        if (r.AssigneeUserId is not null)
        {
            item.AssignToUser(r.AssigneeUserId);
        }
        else if (r.AssigneeAgentId is null)
        {
            item.AssignToUser(null);
        }

        // 显式写入，返回的 UpdatedAt 就是落库值，客户端可以直接用它作为下一次编辑的版本。
        item.UpdatedAt = now;
        await workflow.OnAssignedAsync(item, previousAgent, tenant.Actor, cancellationToken).ConfigureAwait(false);
        await workflow.OnStatusChangedAsync(item, previousStatus, tenant.Actor, cancellationToken).ConfigureAwait(false);
        changes.Publish(RealtimeEntities.WorkItem, item.Id, RealtimeChangeKind.Updated);
        var dto = await ToDtoAsync(item, cancellationToken).ConfigureAwait(false);
        if (previousStatus != item.Status)
        {
            await PublishAsync(PlatformEventKeys.TaskStatusChanged, item, dto, cancellationToken, ("from", previousStatus), ("to", item.Status), ("comment", null))
                .ConfigureAwait(false);
        }

        return dto;
    }

    public async Task<WorkItemDto> HandleAsync(ChangeWorkItemStatusCommand request, CancellationToken cancellationToken)
    {
        var item = await FindAsync(request.Id, cancellationToken).ConfigureAwait(false);
        var previous = item.Status;
        item.ChangeStatus(request.Status, clock.GetUtcNow());
        if (!string.IsNullOrWhiteSpace(request.Comment))
        {
            db.WorkItemComments.Add(new WorkItemComment
            {
                WorkItemId = item.Id,
                Author = tenant.Actor,
                Body = request.Comment.Trim(),
                RunId = request.RunId,
            });
        }

        await workflow.OnStatusChangedAsync(item, previous, tenant.Actor, cancellationToken).ConfigureAwait(false);
        changes.Publish(RealtimeEntities.WorkItem, item.Id, RealtimeChangeKind.Updated);
        var dto = await ToDtoAsync(item, cancellationToken).ConfigureAwait(false);
        if (previous != item.Status)
        {
            var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
            await PublishAsync(PlatformEventKeys.TaskStatusChanged, item, dto, cancellationToken, ("from", previous), ("to", item.Status), ("comment", comment))
                .ConfigureAwait(false);
        }

        return dto;
    }

    public async Task<WorkItemDto> HandleAsync(AssignWorkItemCommand request, CancellationToken cancellationToken)
    {
        if (request.AgentId is not null && request.UserId is not null)
        {
            throw new ConflictException("work_item.single_assignee", "任务只能指派给一个负责人。");
        }

        var item = await FindAsync(request.Id, cancellationToken).ConfigureAwait(false);
        await EnsureUserIsMemberAsync(request.UserId, cancellationToken).ConfigureAwait(false);
        var previousAgent = item.AssigneeAgentId;
        if (request.UserId is not null)
        {
            item.AssignToUser(request.UserId);
        }
        else
        {
            item.AssignToAgent(request.AgentId);
            if (request.AgentId is null)
            {
                item.AssignToUser(null);
            }
        }

        await workflow.OnAssignedAsync(item, previousAgent, tenant.Actor, cancellationToken).ConfigureAwait(false);
        changes.Publish(RealtimeEntities.WorkItem, item.Id, RealtimeChangeKind.Updated);
        return await ToDtoAsync(item, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CommentDto> HandleAsync(AddCommentCommand request, CancellationToken cancellationToken)
    {
        var item = await FindAsync(request.WorkItemId, cancellationToken).ConfigureAwait(false);
        var comment = new WorkItemComment
        {
            WorkItemId = item.Id,
            Author = tenant.Actor,
            Body = request.Body.Trim(),
            RunId = request.RunId,
        };
        db.WorkItemComments.Add(comment);
        await workflow.OnCommentedAsync(item, tenant.Actor, comment.Body, cancellationToken).ConfigureAwait(false);
        var actors = await ActorNames.ResolveAsync(db, [comment.Author], cancellationToken).ConfigureAwait(false);
        var dto = comment.ToDto(actors);
        await PublishAsync(PlatformEventKeys.TaskCommented, item, null, cancellationToken, ("comment", dto)).ConfigureAwait(false);
        return dto;
    }

    /// <summary>
    /// 发布任务事件（订阅的工作流随本事务一起创建运行）。载荷只在有订阅者时构造。
    /// </summary>
    private Task PublishAsync(string eventType, WorkItem item, WorkItemDto? dto, CancellationToken cancellationToken, params (string Key, object? Value)[] fields)
        => events.PublishAsync(eventType, item.Id, async () =>
        {
            var task = dto ?? await ToDtoAsync(item, cancellationToken).ConfigureAwait(false);
            var actors = await ActorNames.ResolveAsync(db, [tenant.Actor], cancellationToken).ConfigureAwait(false);
            var payload = new Dictionary<string, object?> { ["task"] = task, ["actor"] = actors(tenant.Actor) };
            foreach (var (key, value) in fields)
            {
                payload[key] = value;
            }

            return payload;
        }, cancellationToken);

    private async Task EnsureUserIsMemberAsync(Guid? userId, CancellationToken cancellationToken)
    {
        if (userId is { } id && !await db.Memberships.AnyAsync(m => m.UserId == id, cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException("member", id);
        }
    }

    private async Task<WorkItem> FindAsync(Guid id, CancellationToken cancellationToken)
        => await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, cancellationToken).ConfigureAwait(false)
           ?? throw new NotFoundException("work_item", id);

    private async Task<WorkItemDto> ToDtoAsync(WorkItem item, CancellationToken cancellationToken)
    {
        var actors = await ActorNames.ResolveAsync(db, [item.CreatedBy], cancellationToken).ConfigureAwait(false);
        return item.ToDto(actors);
    }
}

internal sealed partial class WorkItemQueryHandlers(IAppDbContext db, ITextMatcher text) :
    IRequestHandler<ListWorkItemsQuery, PagedResult<WorkItemDto>>,
    IRequestHandler<GetWorkItemQuery, WorkItemDetailDto>
{
    public async Task<PagedResult<WorkItemDto>> HandleAsync(ListWorkItemsQuery request, CancellationToken cancellationToken)
    {
        var (page, pageSize) = Paging.Normalize(request.Page, request.PageSize);
        var query = db.WorkItems.AsNoTracking();
        if (request.Status is { } status)
        {
            query = query.Where(w => w.Status == status);
        }

        if (request.OpenOnly)
        {
            query = query.Where(w => w.Status != WorkItemStatus.Done && w.Status != WorkItemStatus.Cancelled);
        }

        if (request.AssigneeAgentId is { } agentId)
        {
            query = query.Where(w => w.AssigneeAgentId == agentId);
        }

        if (request.AssigneeUserId is { } userId)
        {
            query = query.Where(w => w.AssigneeUserId == userId);
        }

        if (request.Priority is { } priority)
        {
            query = query.Where(w => w.Priority == priority);
        }

        if (request.ParentId is { } parentId)
        {
            query = query.Where(w => w.ParentId == parentId);
        }

        if (request.ProjectId is { } projectId)
        {
            query = query.Where(w => w.ProjectId == projectId);
        }

        if (request.MilestoneId is { } milestoneId)
        {
            query = query.Where(w => w.MilestoneId == milestoneId);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            // 输入任务编号（T-12）直接定位；其余按标题和描述模糊匹配（走三元组索引，% 和 _ 按字面匹配）。
            var term = request.Search.Trim();
            query = TaskKey().Match(term) is { Success: true } key && int.TryParse(key.Groups[1].ValueSpan, out var number)
                ? query.Where(w => w.Number == number)
                : text.WhereContains(query, term, w => w.Title, w => w.Description);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query
            .OrderByDescending(w => w.Priority)
            .ThenByDescending(w => w.Number)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var actors = await ActorNames.ResolveAsync(db, items.Select(i => i.CreatedBy), cancellationToken).ConfigureAwait(false);
        return new PagedResult<WorkItemDto>([.. items.Select(i => i.ToDto(actors))], total, page, pageSize);
    }

    public async Task<WorkItemDetailDto> HandleAsync(GetWorkItemQuery request, CancellationToken cancellationToken)
    {
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == request.Id, cancellationToken).ConfigureAwait(false)
                   ?? throw new NotFoundException("work_item", request.Id);
        var subtasks = await db.WorkItems.AsNoTracking().Where(w => w.ParentId == item.Id).OrderBy(w => w.Number)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var comments = await db.WorkItemComments.AsNoTracking().Where(c => c.WorkItemId == item.Id).OrderBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var predecessors = await db.WorkItems.AsNoTracking()
            .Where(w => db.WorkItemDependencies.Any(d => d.WorkItemId == item.Id && d.DependsOnId == w.Id))
            .OrderBy(w => w.Number)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var dependents = await db.WorkItems.AsNoTracking()
            .Where(w => db.WorkItemDependencies.Any(d => d.DependsOnId == item.Id && d.WorkItemId == w.Id))
            .OrderBy(w => w.Number)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var actors = await ActorNames.ResolveAsync(
            db,
            new[] { item.CreatedBy }.Concat(subtasks.Select(s => s.CreatedBy)).Concat(comments.Select(c => c.Author)),
            cancellationToken).ConfigureAwait(false);
        return new WorkItemDetailDto(
            item.ToDto(actors),
            [.. subtasks.Select(s => s.ToDto(actors))],
            [.. comments.Select(c => c.ToDto(actors))],
            [.. predecessors.Select(w => w.ToLink())],
            [.. dependents.Select(w => w.ToLink())]);
    }

    [GeneratedRegex(@"^[Tt]-?(\d{1,9})$")]
    private static partial Regex TaskKey();
}
