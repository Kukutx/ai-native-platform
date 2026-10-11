using System.Text.Json;
using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Common;
using AiNative.Application.Modules;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Application.Workflows.Expressions;
using AiNative.Domain.Common;
using AiNative.Domain.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiNative.Application.Workflows;

/// <summary>
/// 平台事件进入工作流的入口：恢复正在等待该事件主体的运行，并为订阅该事件的已启用工作流创建新运行。
/// 在发布事件的写操作的事务里执行，新运行和业务变化一起提交；发布方不在事务里时自己开一个。
/// </summary>
internal sealed class WorkflowTriggers(
    IAppDbContext db,
    ITenantContext tenant,
    IModuleAccess modules,
    IModuleCatalog catalog,
    WorkflowOwnerAccess owners,
    WorkflowExecutionScope scope,
    ITransactionLock locks,
    IUnitOfWork unitOfWork,
    IPostCommitActions postCommit,
    IWorkflowSignal signal,
    IChangeFeed changes,
    IOptions<WorkflowOptions> options,
    TimeProvider clock,
    ILogger<WorkflowTriggers> logger) : IPlatformEvents
{
    public async Task PublishAsync(string eventType, Guid subjectId, Func<Task<object>> payload, CancellationToken cancellationToken = default)
    {
        if (!tenant.HasTenant)
        {
            return;
        }

        // 等待锁和触发锁都是事务级的。审批处理等不走统一事务管道的发布方在这里补一个事务。
        if (db.Database.CurrentTransaction is null)
        {
            await unitOfWork.RunAsync(async () =>
            {
                await PublishInTransactionAsync(eventType, subjectId, payload, cancellationToken).ConfigureAwait(false);
                return Unit.Value;
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await PublishInTransactionAsync(eventType, subjectId, payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishInTransactionAsync(string eventType, Guid subjectId, Func<Task<object>> payload, CancellationToken cancellationToken)
    {
        var resumed = await ResumeWaitingAsync(eventType, subjectId, cancellationToken).ConfigureAwait(false);
        var started = await StartSubscribedAsync(eventType, payload, cancellationToken).ConfigureAwait(false);
        if (resumed || started)
        {
            postCommit.Enqueue(_ =>
            {
                signal.Notify();
                return Task.CompletedTask;
            });
        }
    }

    /// <summary>
    /// 与执行器检查等待条件时持有同一把锁：要么执行器先登记"等待"、这里随后把它放回队列，
    /// 要么这里先提交、执行器随后读到已完成的状态，不会出现两边都错过的情况。
    /// </summary>
    private async Task<bool> ResumeWaitingAsync(string eventType, Guid subjectId, CancellationToken cancellationToken)
    {
        IQueryable<WorkflowRun> waiting;
        switch (eventType)
        {
            case PlatformEventKeys.TaskStatusChanged:
                waiting = db.WorkflowRuns.Where(r => r.Status == WorkflowRunStatus.Waiting && r.WaitingWorkItemId == subjectId);
                break;
            case PlatformEventKeys.ApprovalDecided:
                waiting = db.WorkflowRuns.Where(r => r.Status == WorkflowRunStatus.Waiting && r.WaitingApprovalId == subjectId);
                break;
            default:
                return false;
        }

        await locks.AcquireAsync(WaitLockKey(subjectId), cancellationToken).ConfigureAwait(false);

        var now = clock.GetUtcNow();
        var ids = await waiting.Select(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (ids.Count == 0)
        {
            return false;
        }

        await db.WorkflowRuns
            .Where(r => ids.Contains(r.Id) && r.Status == WorkflowRunStatus.Waiting)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, WorkflowRunStatus.Queued).SetProperty(r => r.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        foreach (var id in ids)
        {
            changes.Publish(RealtimeEntities.WorkflowRun, id, RealtimeChangeKind.Updated);
        }

        return true;
    }

    private async Task<bool> StartSubscribedAsync(string eventType, Func<Task<object>> payload, CancellationToken cancellationToken)
    {
        if (catalog.FindEvent(eventType) is not { } definition
            || !await modules.IsEnabledAsync(WorkflowsModule.ModuleKey, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // 按 ID 顺序加锁，多个事务同时发布事件时加锁顺序一致。
        var candidates = await db.Workflows.AsNoTracking()
            .Where(w => w.IsEnabled && w.TriggerKind == WorkflowTriggerKind.Event && w.EventType == eventType)
            .OrderBy(w => w.Id)
            .Select(w => w.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return false;
        }

        if (scope.ChildDepth > options.Value.MaxTriggerDepth)
        {
            logger.LogWarning("Event {EventType} from workflow run {RunId} exceeds trigger depth; not starting workflows", eventType, scope.RunId);
            return false;
        }

        JsonElement? data = null;
        var context = default(JsonElement);
        var ownerGrants = new Dictionary<Actor, bool>();
        var since = clock.GetUtcNow().AddMinutes(-1);
        var started = false;
        foreach (var workflowId in candidates)
        {
            // 工作流自己的步骤引发的事件不再触发它自己。
            if (workflowId == scope.WorkflowId)
            {
                continue;
            }

            // 同一工作流的触发与删除串行：频率计数和新建运行是原子的，也不会给刚删除的工作流插入运行。
            await locks.AcquireAsync(TriggerLockKey(workflowId), cancellationToken).ConfigureAwait(false);
            var workflow = await db.Workflows.AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == workflowId && w.IsEnabled && w.TriggerKind == WorkflowTriggerKind.Event && w.EventType == eventType, cancellationToken)
                .ConfigureAwait(false);
            if (workflow is null || !await OwnerCanReceiveAsync(workflow, definition, ownerGrants, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            if (data is null)
            {
                data = PlatformJson.ToElement(await payload().ConfigureAwait(false));
                context = PlatformJson.ToElement(new { trigger = data });
            }

            if (!Matches(workflow, context))
            {
                continue;
            }

            var recent = await db.WorkflowRuns.CountAsync(r => r.WorkflowId == workflow.Id && r.TriggerKind == WorkflowTriggerKind.Event && r.CreatedAt > since, cancellationToken)
                .ConfigureAwait(false);
            if (recent >= options.Value.MaxEventRunsPerMinute)
            {
                logger.LogWarning("Workflow {WorkflowId} hit the per-minute trigger limit; ignoring event {EventType}", workflow.Id, eventType);
                continue;
            }

            var run = WorkflowRuns.Create(workflow, WorkflowTriggerKind.Event, eventType, tenant.Actor, data.Value, default, scope);
            db.WorkflowRuns.Add(run);
            changes.Publish(RealtimeEntities.WorkflowRun, run.Id, RealtimeChangeKind.Created);
            started = true;
        }

        if (started)
        {
            // 立即写入，同一事务里随后发布的事件计数时能看到这些运行。
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return started;
    }

    /// <summary>
    /// 载荷里是所有者能看到的业务数据，所以每次触发都按所有者当前的权限复查：
    /// 保存工作流之后被收回事件权限、离开空间或被停用的所有者，不再收到这类事件。
    /// </summary>
    private async Task<bool> OwnerCanReceiveAsync(Workflow workflow, PlatformEventDefinition definition, Dictionary<Actor, bool> cache, CancellationToken cancellationToken)
    {
        if (!cache.TryGetValue(workflow.Owner, out var granted))
        {
            try
            {
                var principal = await owners.ResolveAsync(workflow.Owner, tenant.TenantId, cancellationToken).ConfigureAwait(false);
                granted = FeatureMatcher.IsGranted(principal.Features, definition.RequiredFeature);
            }
            catch (WorkflowRunException)
            {
                granted = false;
            }

            cache[workflow.Owner] = granted;
        }

        if (!granted)
        {
            logger.LogWarning("Owner of workflow {WorkflowId} can no longer receive event {EventType}; skipping", workflow.Id, definition.Key);
        }

        return granted;
    }

    private bool Matches(Workflow workflow, JsonElement context)
    {
        if (string.IsNullOrWhiteSpace(workflow.EventFilter))
        {
            return true;
        }

        try
        {
            return WorkflowExpression.Parse(workflow.EventFilter).IsTrue(context);
        }
        catch (WorkflowExpressionException ex)
        {
            logger.LogWarning("Workflow {WorkflowId} has an invalid event filter: {Message}", workflow.Id, ex.Message);
            return false;
        }
    }

    public static string WaitLockKey(Guid subjectId) => $"workflow-wait:{subjectId}";

    public static string TriggerLockKey(Guid workflowId) => $"workflow-trigger:{workflowId}";
}
