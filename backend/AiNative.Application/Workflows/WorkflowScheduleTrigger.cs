using AiNative.Application.Abstractions;
using AiNative.Application.Common;
using AiNative.Application.Modules;
using AiNative.Application.Realtime;
using AiNative.Domain.Common;
using AiNative.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Workflows;

/// <summary>
/// 定时触发：把到期工作流的下一次时间往后推，并创建一次运行。由引擎在租户作用域和事务里调用。
/// </summary>
public interface IWorkflowScheduleTrigger
{
    /// <returns>是否创建了运行。</returns>
    Task<bool> FireAsync(Guid workflowId, DateTimeOffset dueAt, CancellationToken cancellationToken);
}

internal sealed class WorkflowScheduleTrigger(
    IAppDbContext db,
    IModuleAccess modules,
    WorkflowExecutionScope scope,
    IChangeFeed changes,
    TimeProvider clock) : IWorkflowScheduleTrigger
{
    public async Task<bool> FireAsync(Guid workflowId, DateTimeOffset dueAt, CancellationToken cancellationToken)
    {
        var workflow = await db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workflowId, cancellationToken).ConfigureAwait(false);
        if (workflow is null || workflow.NextRunAt != dueAt)
        {
            return false;
        }

        // 条件更新认领这一次触发：多个调度循环同时看到同一条到期记录时只有一个能成功。
        // 错过的多次（例如服务停机期间）只补跑一次。不改 UpdatedAt，它是编辑工作流时的并发版本。
        var next = WorkflowSchedule.NextRun(workflow, clock.GetUtcNow());
        var claimed = await db.Workflows
            .Where(w => w.Id == workflowId && w.NextRunAt == dueAt)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.NextRunAt, next), cancellationToken)
            .ConfigureAwait(false);
        if (claimed == 0 || !await modules.IsEnabledAsync(WorkflowsModule.ModuleKey, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var trigger = PlatformJson.ToElement(new { scheduledAt = dueAt, actor = new { kind = ActorKind.System, name = "定时触发" } });
        var run = WorkflowRuns.Create(workflow, WorkflowTriggerKind.Schedule, null, Actor.System, trigger, default, scope);
        db.WorkflowRuns.Add(run);
        changes.Publish(RealtimeEntities.WorkflowRun, run.Id, RealtimeChangeKind.Created);
        return true;
    }
}
