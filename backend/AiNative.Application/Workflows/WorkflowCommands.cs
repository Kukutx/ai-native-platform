using System.Text.Json;
using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Common;
using AiNative.Application.Governance;
using AiNative.Application.Modules;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Common;
using AiNative.Contracts.Workflows;
using AiNative.Domain.Common;
using AiNative.Domain.Governance;
using AiNative.Domain.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiNative.Application.Workflows;

[RequireFeature(Features.Workflows.View)]
public sealed record ListWorkflowsQuery : IQuery<IReadOnlyList<WorkflowDto>>;

[RequireFeature(Features.Workflows.View)]
public sealed record GetWorkflowQuery(Guid Id) : IQuery<WorkflowDto>;

[RequireFeature(Features.Workflows.View)]
public sealed record GetWorkflowCatalogQuery : IQuery<WorkflowCatalogDto>;

[RequireFeature(Features.Workflows.Manage)]
public sealed record CreateWorkflowCommand(SaveWorkflowRequest Request) : ICommand<WorkflowDto>;

[RequireFeature(Features.Workflows.Manage)]
public sealed record UpdateWorkflowCommand(Guid Id, SaveWorkflowRequest Request) : ICommand<WorkflowDto>;

[RequireFeature(Features.Workflows.Manage)]
public sealed record SetWorkflowEnabledCommand(Guid Id, bool Enabled) : ICommand<WorkflowDto>;

[RequireFeature(Features.Workflows.Manage)]
public sealed record DeleteWorkflowCommand(Guid Id) : ICommand<Unit>;

/// <summary>
/// 换一个新的 Webhook 地址，旧地址立即失效（地址泄露时使用）。
/// </summary>
[RequireFeature(Features.Workflows.Manage)]
public sealed record RegenerateWorkflowWebhookCommand(Guid Id) : ICommand<WorkflowDto>;

[RequireFeature(Features.Workflows.Run)]
public sealed record StartWorkflowRunCommand(Guid WorkflowId, JsonElement? Input) : ICommand<WorkflowRunSummaryDto>;

[RequireFeature(Features.Workflows.Run)]
public sealed record CancelWorkflowRunCommand(Guid RunId) : ICommand<WorkflowRunSummaryDto>;

[RequireFeature(Features.Workflows.View)]
public sealed record ListWorkflowRunsQuery(Guid? WorkflowId = null, WorkflowRunStatus? Status = null, int Page = 1, int PageSize = 30)
    : IQuery<PagedResult<WorkflowRunSummaryDto>>;

[RequireFeature(Features.Workflows.View)]
public sealed record GetWorkflowRunQuery(Guid RunId) : IQuery<WorkflowRunDto>;

internal sealed class WorkflowCommandHandlers(
    IAppDbContext db,
    ITenantContext tenant,
    WorkflowDefinitionValidator validator,
    WorkflowExecutionScope scope,
    ITransactionLock locks,
    IPostCommitActions postCommit,
    IWorkflowSignal signal,
    IChangeFeed changes,
    IServiceProvider services,
    IOptions<WorkflowOptions> options,
    TimeProvider clock) :
    IRequestHandler<CreateWorkflowCommand, WorkflowDto>,
    IRequestHandler<UpdateWorkflowCommand, WorkflowDto>,
    IRequestHandler<SetWorkflowEnabledCommand, WorkflowDto>,
    IRequestHandler<DeleteWorkflowCommand, Unit>,
    IRequestHandler<RegenerateWorkflowWebhookCommand, WorkflowDto>,
    IRequestHandler<StartWorkflowRunCommand, WorkflowRunSummaryDto>,
    IRequestHandler<CancelWorkflowRunCommand, WorkflowRunSummaryDto>
{
    public async Task<WorkflowDto> HandleAsync(CreateWorkflowCommand request, CancellationToken cancellationToken)
    {
        var (trigger, steps) = await validator.ValidateAsync(request.Request, cancellationToken).ConfigureAwait(false);
        var workflow = new Workflow { Name = request.Request.Name.Trim(), Owner = RequireOwner() };
        Apply(workflow, request.Request, trigger, steps);
        db.Workflows.Add(workflow);
        changes.Publish(RealtimeEntities.Workflow, workflow.Id, RealtimeChangeKind.Created);
        return await ToDtoAsync(workflow, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkflowDto> HandleAsync(UpdateWorkflowCommand request, CancellationToken cancellationToken)
    {
        var workflow = await FindAsync(request.Id, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (request.Request.ExpectedUpdatedAt is { } expected)
        {
            await OptimisticConcurrency.EnsureUnchangedAsync(
                db.Workflows, workflow.Id, expected, now, "workflow.modified", "工作流刚被其他人修改过，已刷新为最新内容，请确认后再保存。", cancellationToken)
                .ConfigureAwait(false);
        }

        var (trigger, steps) = await validator.ValidateAsync(request.Request, cancellationToken).ConfigureAwait(false);
        workflow.Name = request.Request.Name.Trim();
        Apply(workflow, request.Request, trigger, steps);

        // 改了步骤就由改的人负责：运行身份换成本次保存者，不能借用原所有者的权限。
        workflow.Owner = RequireOwner();
        workflow.Version++;
        workflow.UpdatedAt = now;
        changes.Publish(RealtimeEntities.Workflow, workflow.Id, RealtimeChangeKind.Updated);
        return await ToDtoAsync(workflow, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkflowDto> HandleAsync(SetWorkflowEnabledCommand request, CancellationToken cancellationToken)
    {
        var workflow = await FindAsync(request.Id, cancellationToken).ConfigureAwait(false);
        workflow.IsEnabled = request.Enabled;
        workflow.NextRunAt = WorkflowSchedule.NextRun(workflow, clock.GetUtcNow());
        changes.Publish(RealtimeEntities.Workflow, workflow.Id, RealtimeChangeKind.Updated);
        return await ToDtoAsync(workflow, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Unit> HandleAsync(DeleteWorkflowCommand request, CancellationToken cancellationToken)
    {
        // 与事件触发持有同一把锁：正在为它创建运行的事务提交之后才删除，不会出现插入运行时外键失效。
        await locks.AcquireAsync(WorkflowTriggers.TriggerLockKey(request.Id), cancellationToken).ConfigureAwait(false);
        var workflow = await FindAsync(request.Id, cancellationToken).ConfigureAwait(false);
        var active = await db.WorkflowRuns.AsNoTracking()
            .Where(r => r.WorkflowId == workflow.Id && (r.Status == WorkflowRunStatus.Queued || r.Status == WorkflowRunStatus.Running || r.Status == WorkflowRunStatus.Waiting))
            .Select(r => r.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var runId in active)
        {
            await CancelAsync(runId, "所属工作流已删除", cancellationToken).ConfigureAwait(false);
        }

        // 运行记录随工作流级联删除。
        db.Workflows.Remove(workflow);
        changes.Publish(RealtimeEntities.Workflow, workflow.Id, RealtimeChangeKind.Deleted);
        return Unit.Value;
    }

    public async Task<WorkflowDto> HandleAsync(RegenerateWorkflowWebhookCommand request, CancellationToken cancellationToken)
    {
        var workflow = await FindAsync(request.Id, cancellationToken).ConfigureAwait(false);
        if (workflow.TriggerKind != WorkflowTriggerKind.Webhook)
        {
            throw new ConflictException("workflow.not_webhook", "这个工作流不是 Webhook 触发，没有 Webhook 地址。");
        }

        // 不改 UpdatedAt：它是编辑定义时的并发版本，换地址不影响正在编辑的人保存。
        workflow.WebhookToken = WorkflowWebhooks.NewToken();
        changes.Publish(RealtimeEntities.Workflow, workflow.Id, RealtimeChangeKind.Updated);
        return await ToDtoAsync(workflow, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkflowRunSummaryDto> HandleAsync(StartWorkflowRunCommand request, CancellationToken cancellationToken)
    {
        var workflow = await db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == request.WorkflowId, cancellationToken).ConfigureAwait(false)
                       ?? throw new NotFoundException("workflow", request.WorkflowId);
        if (scope.WorkflowId == workflow.Id)
        {
            throw new ConflictException("workflow.recursive", "工作流不能在自己的步骤里运行自己。");
        }

        if (scope.ChildDepth > options.Value.MaxTriggerDepth)
        {
            throw new ConflictException("workflow.too_deep", $"工作流嵌套运行超过 {options.Value.MaxTriggerDepth} 层，已停止。");
        }

        var input = request.Input is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } value ? value : PlatformJson.ToElement(new { });
        if (input.ValueKind != JsonValueKind.Object)
        {
            throw new ValidationFailedException(new Dictionary<string, string[]> { ["input"] = ["运行参数必须是 JSON 对象。"] });
        }

        var actors = await ActorNames.ResolveAsync(db, [tenant.Actor], cancellationToken).ConfigureAwait(false);
        var trigger = PlatformJson.ToElement(new { actor = WorkflowRuns.ActorPayload(tenant.Actor, actors) });
        var run = WorkflowRuns.Create(workflow, WorkflowTriggerKind.Manual, null, tenant.Actor, trigger, input, scope);
        db.WorkflowRuns.Add(run);
        changes.Publish(RealtimeEntities.WorkflowRun, run.Id, RealtimeChangeKind.Created);
        postCommit.Enqueue(_ =>
        {
            signal.Notify();
            return Task.CompletedTask;
        });
        return run.ToSummary(actors);
    }

    public async Task<WorkflowRunSummaryDto> HandleAsync(CancelWorkflowRunCommand request, CancellationToken cancellationToken)
    {
        var actors = await ActorNames.ResolveAsync(db, [tenant.Actor], cancellationToken).ConfigureAwait(false);
        if (!await CancelAsync(request.RunId, $"已被{actors(tenant.Actor).Name ?? "系统"}取消", cancellationToken).ConfigureAwait(false))
        {
            throw new ConflictException("workflow_run.finished", "这次运行已经结束。");
        }

        var run = await db.WorkflowRuns.AsNoTracking().FirstAsync(r => r.Id == request.RunId, cancellationToken).ConfigureAwait(false);
        actors = await ActorNames.ResolveAsync(db, [run.TriggeredBy], cancellationToken).ConfigureAwait(false);
        return run.ToSummary(actors);
    }

    /// <summary>
    /// 取消未结束的运行。正在执行的步骤提交进度时会发现运行已取消而整体回滚；等待中的审批一并拒绝，
    /// 避免之后有人批准时动作仍被执行。审批已通过、动作正在执行时拒绝取消，否则动作结果将无人接手。
    /// </summary>
    private async Task<bool> CancelAsync(Guid runId, string reason, CancellationToken cancellationToken)
    {
        if (!await db.WorkflowRuns.AnyAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException("workflow_run", runId);
        }

        var now = clock.GetUtcNow();
        var cancelled = await db.WorkflowRuns
            .Where(r => r.Id == runId && (r.Status == WorkflowRunStatus.Queued || r.Status == WorkflowRunStatus.Running || r.Status == WorkflowRunStatus.Waiting))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, WorkflowRunStatus.Cancelled)
                .SetProperty(r => r.Error, reason)
                .SetProperty(r => r.ResumeAt, (DateTimeOffset?)null)
                .SetProperty(r => r.FinishedAt, now)
                .SetProperty(r => r.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        if (cancelled == 0)
        {
            return false;
        }

        // 上面的更新已锁住这一行，执行器登记等待也要更新它，所以此刻读到的等待审批不会再变。
        var waitingApprovalId = await db.WorkflowRuns.AsNoTracking().Where(r => r.Id == runId).Select(r => r.WaitingApprovalId)
            .FirstAsync(cancellationToken).ConfigureAwait(false);
        if (waitingApprovalId is { } approvalId)
        {
            var rejected = await db.Approvals
                .Where(a => a.Id == approvalId && a.Status == ApprovalStatus.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, ApprovalStatus.Rejected)
                    .SetProperty(a => a.DecidedByUserId, tenant.Actor.IsUser ? tenant.Actor.Id : null)
                    .SetProperty(a => a.DecidedAt, now)
                    .SetProperty(a => a.DecisionNote, $"工作流运行{reason}")
                    .SetProperty(a => a.UpdatedAt, now), cancellationToken)
                .ConfigureAwait(false);
            if (rejected > 0)
            {
                changes.Publish(RealtimeEntities.Approval, approvalId, RealtimeChangeKind.Updated);
                await ApprovalEvents.PublishDecidedAsync(services, approvalId, cancellationToken).ConfigureAwait(false);
            }
            else if (await db.Approvals.AnyAsync(
                         a => a.Id == approvalId && a.Status == ApprovalStatus.Approved && a.ResultJson == null && a.ExecutionError == null,
                         cancellationToken).ConfigureAwait(false))
            {
                // 抛出后整个事务回滚，运行保持原状，动作完成后会照常恢复它。
                throw new ConflictException("workflow_run.approval_executing", "审批已通过，动作正在执行，请等动作完成后再操作。");
            }
        }

        await db.WorkflowRunSteps
            .Where(s => s.RunId == runId && (s.Status == WorkflowStepStatus.Running || s.Status == WorkflowStepStatus.Waiting))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, WorkflowStepStatus.Failed)
                .SetProperty(x => x.Error, reason)
                .SetProperty(x => x.FinishedAt, now)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);

        changes.Publish(RealtimeEntities.WorkflowRun, runId, RealtimeChangeKind.Updated);
        return true;
    }

    private void Apply(Workflow workflow, SaveWorkflowRequest request, WorkflowTriggerDto trigger, List<WorkflowStep> steps)
    {
        workflow.Description = (request.Description ?? string.Empty).Trim();
        workflow.IsEnabled = request.IsEnabled;
        workflow.TriggerKind = trigger.Kind;
        workflow.EventType = trigger.EventType;
        workflow.EventFilter = trigger.Filter;
        workflow.Cron = trigger.Cron;
        workflow.TimeZone = trigger.TimeZone;
        workflow.Steps = steps;
        workflow.NextRunAt = WorkflowSchedule.NextRun(workflow, clock.GetUtcNow());

        // 改成 Webhook 触发时生成地址，之后保存沿用同一个地址；改成别的触发方式时作废。
        workflow.WebhookToken = trigger.Kind == WorkflowTriggerKind.Webhook ? workflow.WebhookToken ?? WorkflowWebhooks.NewToken() : null;
    }

    private Actor RequireOwner()
        => tenant.Actor.Kind is ActorKind.User or ActorKind.Agent
            ? tenant.Actor
            : throw new ForbiddenException("工作流必须由成员或智能体保存，运行时以保存者的身份和权限执行。");

    private async Task<Workflow> FindAsync(Guid id, CancellationToken cancellationToken)
        => await db.Workflows.FirstOrDefaultAsync(w => w.Id == id, cancellationToken).ConfigureAwait(false)
           ?? throw new NotFoundException("workflow", id);

    private async Task<WorkflowDto> ToDtoAsync(Workflow workflow, CancellationToken cancellationToken)
        => (await WorkflowMapping.ToDtosAsync(db, tenant, [workflow], cancellationToken).ConfigureAwait(false))[0];
}

internal sealed class WorkflowQueryHandlers(IAppDbContext db, IModuleCatalog catalog, IModuleAccess modules, ITenantContext tenant) :
    IRequestHandler<ListWorkflowsQuery, IReadOnlyList<WorkflowDto>>,
    IRequestHandler<GetWorkflowQuery, WorkflowDto>,
    IRequestHandler<GetWorkflowCatalogQuery, WorkflowCatalogDto>,
    IRequestHandler<ListWorkflowRunsQuery, PagedResult<WorkflowRunSummaryDto>>,
    IRequestHandler<GetWorkflowRunQuery, WorkflowRunDto>
{
    public async Task<IReadOnlyList<WorkflowDto>> HandleAsync(ListWorkflowsQuery request, CancellationToken cancellationToken)
    {
        var workflows = await db.Workflows.AsNoTracking().OrderBy(w => w.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        return await WorkflowMapping.ToDtosAsync(db, tenant, workflows, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkflowDto> HandleAsync(GetWorkflowQuery request, CancellationToken cancellationToken)
    {
        var workflow = await db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == request.Id, cancellationToken).ConfigureAwait(false)
                       ?? throw new NotFoundException("workflow", request.Id);
        return (await WorkflowMapping.ToDtosAsync(db, tenant, [workflow], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<WorkflowCatalogDto> HandleAsync(GetWorkflowCatalogQuery request, CancellationToken cancellationToken)
    {
        var enabled = await modules.GetEnabledAsync(cancellationToken).ConfigureAwait(false);
        return new WorkflowCatalogDto(
        [
            .. catalog.AllEvents
                .Where(e => enabled.Contains(e.ModuleKey) && tenant.Can(e.RequiredFeature))
                .Select(e => new WorkflowEventDto(e.Key, e.Name, e.ModuleKey, e.Description)),
        ]);
    }

    public async Task<PagedResult<WorkflowRunSummaryDto>> HandleAsync(ListWorkflowRunsQuery request, CancellationToken cancellationToken)
    {
        var (page, pageSize) = Paging.Normalize(request.Page, request.PageSize, 100);
        var query = db.WorkflowRuns.AsNoTracking();
        if (request.WorkflowId is { } workflowId)
        {
            query = query.Where(r => r.WorkflowId == workflowId);
        }

        if (request.Status is { } status)
        {
            query = query.Where(r => r.Status == status);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var runs = await query.OrderByDescending(r => r.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var actors = await ActorNames.ResolveAsync(db, runs.Select(r => r.TriggeredBy), cancellationToken).ConfigureAwait(false);
        return new PagedResult<WorkflowRunSummaryDto>([.. runs.Select(r => r.ToSummary(actors))], total, page, pageSize);
    }

    public async Task<WorkflowRunDto> HandleAsync(GetWorkflowRunQuery request, CancellationToken cancellationToken)
    {
        var run = await db.WorkflowRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.RunId, cancellationToken).ConfigureAwait(false)
                  ?? throw new NotFoundException("workflow_run", request.RunId);
        var records = await db.WorkflowRunSteps.AsNoTracking().Where(s => s.RunId == run.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var steps = records.OrderBy(s => s.Path, StepPathComparer.Instance).ToList();
        var actors = await ActorNames.ResolveAsync(db, [run.TriggeredBy, run.Owner], cancellationToken).ConfigureAwait(false);
        return new WorkflowRunDto(
            run.ToSummary(actors),
            actors(run.Owner),
            run.WorkflowVersion,
            PlatformJson.Parse(run.TriggerJson),
            PlatformJson.Parse(run.InputJson),
            tenant.Can(Features.Workflows.Manage) ? run.Steps : HttpStepSecurity.MaskSensitiveHeaders(run.Steps),
            [.. steps.Select(s => s.ToDto())],
            run.WaitingApprovalId,
            run.WaitingWorkItemId,
            run.ResumeAt,
            run.ParentRunId);
    }
}
