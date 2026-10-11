using System.Text.Json;
using AiNative.Application.Abstractions;
using AiNative.Application.Common;
using AiNative.Application.Modules;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Application.Workflows.Expressions;
using AiNative.Domain.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiNative.Application.Workflows;

/// <summary>
/// 平台事件进入工作流的入口：恢复正在等待该事件主体的运行，并为订阅该事件的已启用工作流创建新运行。
/// 在发布事件的写操作的事务里执行，新运行和业务变化一起提交。
/// </summary>
internal sealed class WorkflowTriggers(
    IAppDbContext db,
    ITenantContext tenant,
    IModuleAccess modules,
    WorkflowExecutionScope scope,
    ITransactionLock locks,
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

        if (db.Database.CurrentTransaction is not null)
        {
            await locks.AcquireAsync(WaitLockKey(subjectId), cancellationToken).ConfigureAwait(false);
        }

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
        if (!await modules.IsEnabledAsync(WorkflowsModule.ModuleKey, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var subscribed = await db.Workflows.AsNoTracking()
            .Where(w => w.IsEnabled && w.TriggerKind == WorkflowTriggerKind.Event && w.EventType == eventType)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (subscribed.Count == 0)
        {
            return false;
        }

        if (scope.ChildDepth > options.Value.MaxTriggerDepth)
        {
            logger.LogWarning("Event {EventType} from workflow run {RunId} exceeds trigger depth; not starting workflows", eventType, scope.RunId);
            return false;
        }

        var data = PlatformJson.ToElement(await payload().ConfigureAwait(false));
        var context = PlatformJson.ToElement(new { trigger = data });
        var since = clock.GetUtcNow().AddMinutes(-1);
        var started = false;
        foreach (var workflow in subscribed)
        {
            // 工作流自己的步骤引发的事件不再触发它自己。
            if (workflow.Id == scope.WorkflowId || !Matches(workflow, context))
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

            var run = WorkflowRuns.Create(workflow, WorkflowTriggerKind.Event, eventType, tenant.Actor, data, default, scope);
            db.WorkflowRuns.Add(run);
            changes.Publish(RealtimeEntities.WorkflowRun, run.Id, RealtimeChangeKind.Created);
            started = true;
        }

        if (started)
        {
            // 发布方可能不在统一事务管道里（例如审批处理），这里直接保存；在事务里时随事务提交。
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return started;
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
}
