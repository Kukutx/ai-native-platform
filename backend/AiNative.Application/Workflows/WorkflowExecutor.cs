using System.Text.Json;
using System.Text.Json.Nodes;
using AiNative.Application.Abstractions;
using AiNative.Application.Actions;
using AiNative.Application.Agents;
using AiNative.Application.Ai;
using AiNative.Application.Common;
using AiNative.Application.Platform;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Application.Work;
using AiNative.Application.Workflows.Expressions;
using AiNative.Contracts.Work;
using AiNative.Domain.Agents;
using AiNative.Domain.Common;
using AiNative.Domain.Governance;
using AiNative.Domain.Work;
using AiNative.Domain.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiNative.Application.Workflows;

/// <summary>
/// 执行（或从中断处继续执行）一次工作流运行。调用前引擎已把运行置为 Running，并保证同一运行不会被并发执行。
/// </summary>
public interface IWorkflowExecutor
{
    Task ExecuteAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken);
}

/// <summary>
/// 按顺序执行步骤。关键约定：
/// <list type="bullet">
/// <item>动作和派活以工作流所有者的身份、在独立作用域里执行，副作用与"本步完成、进度前进"在同一事务里提交，
/// 崩溃后重做未提交的步骤不会重复产生副作用。</item>
/// <item>所有进度写入都以"运行仍是 Running 且停在这一步"为条件；运行被取消或删除时条件不成立，整个步骤回滚。</item>
/// <item>需要等待（审批、智能体完成任务、延时）时把运行置为 Waiting 并结束本次执行，条件满足后由事件或引擎重新调度。</item>
/// </list>
/// </summary>
internal sealed class WorkflowExecutor(
    IAppDbContext db,
    TenantContext tenant,
    IUnitOfWork unitOfWork,
    ITransactionLock locks,
    ITenantScopeFactory scopes,
    WorkflowOwnerAccess owners,
    IModelGateway models,
    IChangeFeed changes,
    IWorkflowSignal signal,
    IOptions<WorkflowOptions> options,
    TimeProvider clock,
    ILogger<WorkflowExecutor> logger) : IWorkflowExecutor
{
    private const string LlmSystemPrompt = "你是自动化工作流中的一个处理步骤。直接输出结果本身，不要寒暄，不要解释过程。要求输出 JSON 时只输出合法的 JSON。";

    private readonly WorkflowOptions _options = options.Value;

    public async Task ExecuteAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken)
    {
        tenant.Enter(tenantId, Actor.System, []);
        var run = await db.WorkflowRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
        if (run is not { Status: WorkflowRunStatus.Running })
        {
            return;
        }

        try
        {
            var principal = await owners.ResolveAsync(run.Owner, run.TenantId, cancellationToken).ConfigureAwait(false);
            var records = await db.WorkflowRunSteps.AsNoTracking().Where(s => s.RunId == run.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            var context = new RunContext(run, records);
            for (var index = run.CurrentStep; index < run.Steps.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = run.Steps[index];
                var resuming = records.Any(r => r.Index == index && r.Status == WorkflowStepStatus.Waiting);
                var outcome = resuming
                    ? await ResumeStepAsync(run, step, index, cancellationToken).ConfigureAwait(false)
                    : await StartStepAsync(run, principal, step, index, context.ToElement(), cancellationToken).ConfigureAwait(false);
                if (outcome.Kind != OutcomeKind.Completed)
                {
                    return;
                }

                context.Record(step.Key, outcome);
            }

            await FinishAsync(run).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 服务停止：运行保持 Running，重启后放回队列，从当前步骤继续（未提交的步骤整体重做）。
        }
        catch (WorkflowRunException ex)
        {
            await FailRunAsync(run, ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Workflow run {RunId} crashed", run.Id);
            await FailRunAsync(run, $"执行出错：{ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task<StepOutcome> StartStepAsync(WorkflowRun run, WorkflowPrincipal principal, WorkflowStep step, int index, JsonElement context, CancellationToken cancellationToken)
    {
        try
        {
            if (step.When is { } when && !WorkflowExpression.Parse(when).IsTrue(context))
            {
                return await InUnitAsync(() => CompleteInAsync(db, changes, run, step, index, WorkflowStepStatus.Skipped, null, null, null, cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
            }

            switch (step.Kind)
            {
                case WorkflowStepKind.Action:
                    var input = step.Input is { } template ? WorkflowTemplate.Render(template, context) : PlatformJson.ToElement(new { });
                    return await RunActionAsync(run, principal, step, index, input, cancellationToken).ConfigureAwait(false);

                case WorkflowStepKind.AgentTask:
                    var title = WorkflowTemplate.RenderString(step.Title, context);
                    var instructions = WorkflowTemplate.RenderString(step.Instructions, context);
                    return await RunAgentTaskAsync(run, principal, step, index, title, instructions, cancellationToken).ConfigureAwait(false);

                case WorkflowStepKind.Llm:
                    var prompt = WorkflowTemplate.RenderString(step.Prompt, context);
                    return await RunLlmAsync(run, principal, step, index, prompt, cancellationToken).ConfigureAwait(false);

                case WorkflowStepKind.Delay:
                    var seconds = step.DelaySeconds ?? 0;
                    var delayInput = PlatformJson.ToElement(new { seconds });
                    return await InUnitAsync(async () =>
                    {
                        await WaitInAsync(db, changes, run, step, index, delayInput, null, null, clock.GetUtcNow().AddSeconds(seconds), cancellationToken).ConfigureAwait(false);
                        return StepOutcome.Waiting;
                    }, cancellationToken).ConfigureAwait(false);

                default:
                    return await FailStepAsync(run, step, index, null, $"不支持的步骤类型 {step.Kind}。", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (WorkflowExpressionException ex)
        {
            return await FailStepAsync(run, step, index, null, $"表达式错误：{ex.Message}", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<StepOutcome> RunActionAsync(WorkflowRun run, WorkflowPrincipal principal, WorkflowStep step, int index, JsonElement input, CancellationToken cancellationToken)
    {
        if (!await MarkRunningAsync(run, step, index, input, cancellationToken).ConfigureAwait(false))
        {
            return StepOutcome.Stopped;
        }

        string? error;
        try
        {
            var outcome = await InOwnerScopeAsync(run, principal, async sp =>
            {
                var stepDb = sp.GetRequiredService<IAppDbContext>();
                var feed = sp.GetRequiredService<IChangeFeed>();
                return await sp.GetRequiredService<IUnitOfWork>().RunAsync(async () =>
                {
                    var result = await sp.GetRequiredService<IActionInvoker>().InvokeAsync(
                        step.ActionKey!,
                        input,
                        new ActionInvocationOptions
                        {
                            WorkflowRunId = run.Id,
                            Rationale = $"工作流《{run.WorkflowName}》的步骤「{step.Name}」",
                            ExtraApprovalRequired = principal.ExtraApprovalRequired,
                        },
                        cancellationToken).ConfigureAwait(false);
                    switch (result.Status)
                    {
                        case ActionInvocationStatus.Succeeded:
                            return await CompleteInAsync(stepDb, feed, run, step, index, WorkflowStepStatus.Succeeded, null, result.Output ?? PlatformJson.ToElement<object?>(null), null, cancellationToken)
                                .ConfigureAwait(false);
                        case ActionInvocationStatus.PendingApproval:
                            await WaitInAsync(stepDb, feed, run, step, index, null, result.ApprovalId, null, null, cancellationToken).ConfigureAwait(false);
                            return StepOutcome.Waiting;
                        default:
                            // 失败的动作可能留下半截的跟踪状态，整体回滚后再单独记录失败。
                            throw new StepFailedException(result.Error ?? result.Status.ToString());
                    }
                }, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return outcome;
        }
        catch (RunStoppedException)
        {
            return StepOutcome.Stopped;
        }
        catch (StepFailedException ex)
        {
            error = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Workflow run {RunId} action step {Step} crashed", run.Id, step.Key);
            error = $"执行出错：{ex.Message}";
        }

        return await FailStepAsync(run, step, index, null, error, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StepOutcome> RunAgentTaskAsync(
        WorkflowRun run,
        WorkflowPrincipal principal,
        WorkflowStep step,
        int index,
        string title,
        string instructions,
        CancellationToken cancellationToken)
    {
        var input = PlatformJson.ToElement(new { agentId = step.AgentId, title, instructions, step.WaitForCompletion, step.TimeoutMinutes });
        if (!await MarkRunningAsync(run, step, index, input, cancellationToken).ConfigureAwait(false))
        {
            return StepOutcome.Stopped;
        }

        var footer = step.WaitForCompletion
            ? $"（由工作流《{run.WorkflowName}》自动创建。完成后请把结论写在任务评论里，再把任务标记为完成，工作流会把结论交给后续步骤。）"
            : $"（由工作流《{run.WorkflowName}》自动创建。）";
        var description = string.IsNullOrWhiteSpace(instructions) ? footer : $"{instructions}\n\n{footer}";
        string error;
        try
        {
            return await InOwnerScopeAsync(run, principal, async sp =>
            {
                var stepDb = sp.GetRequiredService<IAppDbContext>();
                var feed = sp.GetRequiredService<IChangeFeed>();
                return await sp.GetRequiredService<IUnitOfWork>().RunAsync(async () =>
                {
                    var task = await sp.GetRequiredService<IRequestDispatcher>().SendAsync(
                        new CreateWorkItemCommand(new CreateWorkItemRequest
                        {
                            Title = title.Length > 300 ? title[..300] : title,
                            Description = description,
                            AssigneeAgentId = step.AgentId,
                        }),
                        cancellationToken).ConfigureAwait(false);
                    if (!step.WaitForCompletion)
                    {
                        var created = PlatformJson.ToElement(new { taskId = task.Id, key = task.Key, title = task.Title, status = task.Status });
                        return await CompleteInAsync(stepDb, feed, run, step, index, WorkflowStepStatus.Succeeded, null, created, null, cancellationToken).ConfigureAwait(false);
                    }

                    var deadline = step.TimeoutMinutes is { } minutes ? clock.GetUtcNow().AddMinutes(minutes) : (DateTimeOffset?)null;
                    await WaitInAsync(stepDb, feed, run, step, index, null, null, task.Id, deadline, cancellationToken).ConfigureAwait(false);
                    return StepOutcome.Waiting;
                }, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (RunStoppedException)
        {
            return StepOutcome.Stopped;
        }
        catch (ValidationFailedException ex)
        {
            error = string.Join("；", ex.Errors.SelectMany(e => e.Value));
        }
        catch (Exception ex) when (ex is AppException or DomainException)
        {
            error = ex.Message;
        }

        return await FailStepAsync(run, step, index, null, error, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StepOutcome> RunLlmAsync(WorkflowRun run, WorkflowPrincipal principal, WorkflowStep step, int index, string prompt, CancellationToken cancellationToken)
    {
        if (!await MarkRunningAsync(run, step, index, PlatformJson.ToElement(new { prompt }), cancellationToken).ConfigureAwait(false))
        {
            return StepOutcome.Stopped;
        }

        // 智能体所有的工作流用该智能体的模型、计入它的预算；成员所有的用平台默认模型。
        var (provider, model) = principal.Agent is { } owner ? (owner.ModelProvider, owner.Model) : (models.Default.Provider, models.Default.Model);
        if (principal.Agent is { MonthlyBudgetUsd: > 0 } budgeted
            && await AgentSpend.ForAgentAsync(db, budgeted.Id, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false) >= budgeted.MonthlyBudgetUsd)
        {
            return await FailStepAsync(run, step, index, null, "所属智能体本月预算已用完。", cancellationToken).ConfigureAwait(false);
        }

        ChatResponse response;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(_options.LlmTimeout);
            try
            {
                response = await models.GetClient(provider, model).GetResponseAsync(
                    [new ChatMessage(ChatRole.System, LlmSystemPrompt), new ChatMessage(ChatRole.User, prompt)],
                    new ChatOptions { ModelId = model, MaxOutputTokens = _options.LlmMaxOutputTokens },
                    timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return await FailStepAsync(run, step, index, null, $"模型调用超过 {_options.LlmTimeout.TotalSeconds:0} 秒未返回。", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Workflow run {RunId} model step {Step} failed", run.Id, step.Key);
                return await FailStepAsync(run, step, index, null, $"模型调用失败：{ex.Message}", cancellationToken).ConfigureAwait(false);
            }
        }

        var text = response.Text?.Trim() ?? string.Empty;
        var inputTokens = response.Usage?.InputTokenCount ?? 0;
        var outputTokens = response.Usage?.OutputTokenCount ?? 0;
        return await InUnitAsync(() =>
        {
            db.CostEvents.Add(new CostEvent
            {
                Actor = principal.Actor,
                Provider = provider,
                Model = model,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                CostUsd = models.EstimateCostUsd(provider, model, inputTokens, outputTokens),
                OccurredAt = clock.GetUtcNow(),
            });
            return CompleteInAsync(db, changes, run, step, index, WorkflowStepStatus.Succeeded, null, LlmOutput(text), null, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 被重新调度的等待中步骤：检查等待的条件是否已经满足。
    /// </summary>
    private async Task<StepOutcome> ResumeStepAsync(WorkflowRun run, WorkflowStep step, int index, CancellationToken cancellationToken)
    {
        Func<Task<Resolution?>> check;
        Guid? subject;
        switch (step.Kind)
        {
            case WorkflowStepKind.Action when run.WaitingApprovalId is { } approvalId:
                subject = approvalId;
                check = () => CheckApprovalAsync(approvalId, cancellationToken);
                break;
            case WorkflowStepKind.AgentTask when run.WaitingWorkItemId is { } workItemId:
                subject = workItemId;
                check = () => CheckWorkItemAsync(run, step, workItemId, cancellationToken);
                break;
            case WorkflowStepKind.Delay:
                subject = null;
                check = () => Task.FromResult<Resolution?>(run.ResumeAt is { } at && at > clock.GetUtcNow()
                    ? null
                    : new Resolution(WorkflowStepStatus.Succeeded, PlatformJson.ToElement(new { waitedSeconds = step.DelaySeconds }), null));
                break;
            default:
                return await FailStepAsync(run, step, index, null, "等待的对象已不存在。", cancellationToken).ConfigureAwait(false);
        }

        var outcome = await InUnitAsync(async () =>
        {
            if (subject is { } id)
            {
                await locks.AcquireAsync(WorkflowTriggers.WaitLockKey(id), cancellationToken).ConfigureAwait(false);
            }

            if (await check().ConfigureAwait(false) is { } resolution)
            {
                return await CompleteInAsync(db, changes, run, step, index, resolution.Status, null, resolution.Output, resolution.Error, cancellationToken).ConfigureAwait(false);
            }

            if (!await TransitionAsync(db, run, index, s => s.SetProperty(r => r.Status, WorkflowRunStatus.Waiting), cancellationToken).ConfigureAwait(false))
            {
                throw new RunStoppedException();
            }

            return StepOutcome.Waiting;
        }, cancellationToken).ConfigureAwait(false);

        // 事务外发布的事件（例如审批结论）不持有上面的锁：重新登记等待之后再查一次，期间已经满足就立刻放回队列。
        if (outcome.Kind == OutcomeKind.Waiting && await check().ConfigureAwait(false) is not null)
        {
            var requeued = await db.WorkflowRuns.Where(r => r.Id == run.Id && r.Status == WorkflowRunStatus.Waiting)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, WorkflowRunStatus.Queued).SetProperty(r => r.UpdatedAt, clock.GetUtcNow()), cancellationToken)
                .ConfigureAwait(false);
            if (requeued > 0)
            {
                signal.Notify();
            }
        }

        return outcome;
    }

    private async Task<Resolution?> CheckApprovalAsync(Guid approvalId, CancellationToken cancellationToken)
    {
        var approval = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == approvalId, cancellationToken).ConfigureAwait(false);
        return approval switch
        {
            null => new Resolution(WorkflowStepStatus.Failed, null, "审批单已不存在。"),
            { Status: ApprovalStatus.Pending } => null,
            { Status: ApprovalStatus.Rejected } => new Resolution(
                WorkflowStepStatus.Failed,
                null,
                string.IsNullOrWhiteSpace(approval.DecisionNote) ? "审批被拒绝。" : $"审批被拒绝：{approval.DecisionNote}"),
            { ResultJson: { } json } => new Resolution(WorkflowStepStatus.Succeeded, PlatformJson.Parse(json), null),
            { ExecutionError: { } error } => new Resolution(WorkflowStepStatus.Failed, null, error),

            // 已批准、动作还在执行，结果写回后会再次唤醒。
            _ => null,
        };
    }

    private async Task<Resolution?> CheckWorkItemAsync(WorkflowRun run, WorkflowStep step, Guid workItemId, CancellationToken cancellationToken)
    {
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return new Resolution(WorkflowStepStatus.Failed, null, "等待的任务已被删除。");
        }

        if (item.Status == WorkItemStatus.Cancelled)
        {
            return new Resolution(WorkflowStepStatus.Failed, null, $"任务 {WorkItemMapping.KeyOf(item.Number)} 被取消。");
        }

        if (item.Status == WorkItemStatus.Done)
        {
            var comments = await db.WorkItemComments.AsNoTracking().Where(c => c.WorkItemId == item.Id)
                .OrderByDescending(c => c.CreatedAt).Take(20).ToListAsync(cancellationToken).ConfigureAwait(false);
            var result = comments.FirstOrDefault(c => c.Author.Kind == ActorKind.Agent && c.Author.Id == item.AssigneeAgentId)?.Body
                         ?? comments.FirstOrDefault()?.Body;
            return new Resolution(
                WorkflowStepStatus.Succeeded,
                PlatformJson.ToElement(new { taskId = item.Id, key = WorkItemMapping.KeyOf(item.Number), title = item.Title, status = item.Status, result }),
                null);
        }

        if (run.ResumeAt is { } deadline && deadline <= clock.GetUtcNow())
        {
            return new Resolution(WorkflowStepStatus.Failed, null, $"任务 {WorkItemMapping.KeyOf(item.Number)} 在 {step.TimeoutMinutes} 分钟内没有完成，等待超时。");
        }

        return null;
    }

    /// <summary>
    /// 以所有者身份在独立作用域中执行；作用域标记为"正在执行该运行"，其中引发的事件据此防止循环触发。
    /// </summary>
    private Task<T> InOwnerScopeAsync<T>(WorkflowRun run, WorkflowPrincipal principal, Func<IServiceProvider, Task<T>> work)
        => scopes.RunAsync(run.TenantId, principal.Actor, principal.Features, sp =>
        {
            sp.GetRequiredService<WorkflowExecutionScope>().Enter(run);
            return work(sp);
        });

    private async Task<StepOutcome> InUnitAsync(Func<Task<StepOutcome>> work, CancellationToken cancellationToken)
    {
        try
        {
            return await unitOfWork.RunAsync(work, cancellationToken).ConfigureAwait(false);
        }
        catch (RunStoppedException)
        {
            return StepOutcome.Stopped;
        }
    }

    private async Task<bool> MarkRunningAsync(WorkflowRun run, WorkflowStep step, int index, JsonElement input, CancellationToken cancellationToken)
    {
        var outcome = await InUnitAsync(async () =>
        {
            if (!await TransitionAsync(db, run, index, _ => { }, cancellationToken).ConfigureAwait(false))
            {
                throw new RunStoppedException();
            }

            await UpsertStepAsync(db, run, step, index, WorkflowStepStatus.Running, input, null, null, finished: false, cancellationToken).ConfigureAwait(false);
            changes.Publish(RealtimeEntities.WorkflowRun, run.Id, RealtimeChangeKind.Updated);
            return new StepOutcome(OutcomeKind.Completed, WorkflowStepStatus.Running);
        }, cancellationToken).ConfigureAwait(false);
        return outcome.Kind != OutcomeKind.Stopped;
    }

    private async Task<StepOutcome> FailStepAsync(WorkflowRun run, WorkflowStep step, int index, JsonElement? input, string error, CancellationToken cancellationToken)
        => await InUnitAsync(() => CompleteInAsync(db, changes, run, step, index, WorkflowStepStatus.Failed, input, null, error, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// 在当前事务里记录一步的结果并推进进度；失败且不允许继续时直接把整个运行标记为失败（同一事务，不会出现"步骤失败但运行继续"）。
    /// </summary>
    private async Task<StepOutcome> CompleteInAsync(
        IAppDbContext stepDb,
        IChangeFeed feed,
        WorkflowRun run,
        WorkflowStep step,
        int index,
        WorkflowStepStatus status,
        JsonElement? input,
        JsonElement? output,
        string? error,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var stopsRun = status == WorkflowStepStatus.Failed && !step.ContinueOnError;
        var moved = stopsRun
            ? await TransitionAsync(stepDb, run, index, s => s
                .SetProperty(r => r.Status, WorkflowRunStatus.Failed)
                .SetProperty(r => r.Error, $"步骤「{step.Name}」失败：{error}")
                .SetProperty(r => r.ResumeAt, (DateTimeOffset?)null)
                .SetProperty(r => r.FinishedAt, now), cancellationToken).ConfigureAwait(false)
            : await TransitionAsync(stepDb, run, index, s => s
                .SetProperty(r => r.CurrentStep, index + 1)
                .SetProperty(r => r.WaitingApprovalId, (Guid?)null)
                .SetProperty(r => r.WaitingWorkItemId, (Guid?)null)
                .SetProperty(r => r.ResumeAt, (DateTimeOffset?)null), cancellationToken).ConfigureAwait(false);
        if (!moved)
        {
            throw new RunStoppedException();
        }

        await UpsertStepAsync(stepDb, run, step, index, status, input, output, error, finished: true, cancellationToken).ConfigureAwait(false);
        feed.Publish(RealtimeEntities.WorkflowRun, run.Id, RealtimeChangeKind.Updated);
        return stopsRun ? StepOutcome.Stopped : new StepOutcome(OutcomeKind.Completed, status, output, error);
    }

    private async Task WaitInAsync(
        IAppDbContext stepDb,
        IChangeFeed feed,
        WorkflowRun run,
        WorkflowStep step,
        int index,
        JsonElement? input,
        Guid? approvalId,
        Guid? workItemId,
        DateTimeOffset? resumeAt,
        CancellationToken cancellationToken)
    {
        var moved = await TransitionAsync(stepDb, run, index, s => s
            .SetProperty(r => r.Status, WorkflowRunStatus.Waiting)
            .SetProperty(r => r.WaitingApprovalId, approvalId)
            .SetProperty(r => r.WaitingWorkItemId, workItemId)
            .SetProperty(r => r.ResumeAt, resumeAt), cancellationToken).ConfigureAwait(false);
        if (!moved)
        {
            throw new RunStoppedException();
        }

        await UpsertStepAsync(stepDb, run, step, index, WorkflowStepStatus.Waiting, input, null, null, finished: false, cancellationToken).ConfigureAwait(false);
        feed.Publish(RealtimeEntities.WorkflowRun, run.Id, RealtimeChangeKind.Updated);
    }

    /// <summary>
    /// 条件更新运行：只有仍在执行且停在这一步时才生效。
    /// </summary>
    private async Task<bool> TransitionAsync(
        IAppDbContext stepDb,
        WorkflowRun run,
        int index,
        Action<UpdateSettersBuilder<WorkflowRun>> set,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var affected = await stepDb.WorkflowRuns
            .Where(r => r.Id == run.Id && r.Status == WorkflowRunStatus.Running && r.CurrentStep == index)
            .ExecuteUpdateAsync(
                s =>
                {
                    set(s);
                    s.SetProperty(r => r.UpdatedAt, now);
                },
                cancellationToken)
            .ConfigureAwait(false);
        return affected == 1;
    }

    private async Task UpsertStepAsync(
        IAppDbContext stepDb,
        WorkflowRun run,
        WorkflowStep step,
        int index,
        WorkflowStepStatus status,
        JsonElement? input,
        JsonElement? output,
        string? error,
        bool finished,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var inputJson = input?.GetRawText();
        var outputJson = output?.GetRawText();
        var exists = await stepDb.WorkflowRunSteps.AnyAsync(s => s.RunId == run.Id && s.Index == index, cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            stepDb.WorkflowRunSteps.Add(new WorkflowRunStep
            {
                RunId = run.Id,
                Index = index,
                StepKey = step.Key,
                Name = step.Name,
                Kind = step.Kind,
                Status = status,
                InputJson = inputJson,
                OutputJson = outputJson,
                Error = error,
                StartedAt = now,
                FinishedAt = finished ? now : null,
            });
            return;
        }

        await stepDb.WorkflowRunSteps
            .Where(s => s.RunId == run.Id && s.Index == index)
            .ExecuteUpdateAsync(
                s =>
                {
                    s.SetProperty(x => x.Status, status);
                    s.SetProperty(x => x.OutputJson, outputJson);
                    s.SetProperty(x => x.Error, error);
                    s.SetProperty(x => x.FinishedAt, finished ? now : null);
                    s.SetProperty(x => x.UpdatedAt, now);
                    if (inputJson is not null)
                    {
                        s.SetProperty(x => x.InputJson, inputJson);
                        s.SetProperty(x => x.StartedAt, now);
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task FinishAsync(WorkflowRun run)
    {
        var now = clock.GetUtcNow();
        var finished = await db.WorkflowRuns
            .Where(r => r.Id == run.Id && r.Status == WorkflowRunStatus.Running && r.CurrentStep == run.Steps.Count)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, WorkflowRunStatus.Succeeded)
                .SetProperty(r => r.FinishedAt, now)
                .SetProperty(r => r.UpdatedAt, now), CancellationToken.None)
            .ConfigureAwait(false);
        if (finished > 0)
        {
            changes.Publish(RealtimeEntities.WorkflowRun, run.Id, RealtimeChangeKind.Updated);
        }
    }

    private async Task FailRunAsync(WorkflowRun run, string error)
    {
        try
        {
            var now = clock.GetUtcNow();
            var failed = await db.WorkflowRuns
                .Where(r => r.Id == run.Id && r.Status == WorkflowRunStatus.Running)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, WorkflowRunStatus.Failed)
                    .SetProperty(r => r.Error, error)
                    .SetProperty(r => r.FinishedAt, now)
                    .SetProperty(r => r.UpdatedAt, now), CancellationToken.None)
                .ConfigureAwait(false);
            if (failed == 0)
            {
                return;
            }

            await db.WorkflowRunSteps
                .Where(s => s.RunId == run.Id && s.Status == WorkflowStepStatus.Running)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, WorkflowStepStatus.Failed)
                    .SetProperty(x => x.Error, error)
                    .SetProperty(x => x.FinishedAt, now)
                    .SetProperty(x => x.UpdatedAt, now), CancellationToken.None)
                .ConfigureAwait(false);
            changes.Publish(RealtimeEntities.WorkflowRun, run.Id, RealtimeChangeKind.Updated);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mark workflow run {RunId} as failed", run.Id);
        }
    }

    /// <summary>
    /// 模型步骤的输出：text 总是有；内容是 JSON 对象或数组时（允许包在 ``` 代码块里）另给解析后的 json，后续步骤可以按字段引用。
    /// </summary>
    private static JsonElement LlmOutput(string text)
    {
        var candidate = text;
        if (candidate.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineEnd = candidate.IndexOf('\n', StringComparison.Ordinal);
            var closing = candidate.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLineEnd > 0 && closing > firstLineEnd)
            {
                candidate = candidate[(firstLineEnd + 1)..closing].Trim();
            }
        }

        if (candidate.StartsWith('{') || candidate.StartsWith('['))
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                return PlatformJson.ToElement(new { text, json = document.RootElement.Clone() });
            }
            catch (JsonException)
            {
            }
        }

        return PlatformJson.ToElement(new { text });
    }

    private sealed record Resolution(WorkflowStepStatus Status, JsonElement? Output, string? Error);

    private enum OutcomeKind
    {
        Completed,
        Waiting,
        Stopped,
    }

    private sealed record StepOutcome(OutcomeKind Kind, WorkflowStepStatus Status = default, JsonElement? Output = null, string? Error = null)
    {
        public static readonly StepOutcome Waiting = new(OutcomeKind.Waiting);

        public static readonly StepOutcome Stopped = new(OutcomeKind.Stopped);
    }

    /// <summary>
    /// 运行已不在执行中（被取消、删除或已结束），当前步骤的事务需要整体回滚。
    /// </summary>
    private sealed class RunStoppedException : Exception;

    private sealed class StepFailedException(string message) : Exception(message);

    /// <summary>
    /// 表达式求值用的运行上下文：workflow、run、trigger、input 与已完成步骤的 steps.&lt;key&gt;。
    /// </summary>
    private sealed class RunContext
    {
        private readonly JsonObject _root;
        private readonly JsonObject _steps = [];

        public RunContext(WorkflowRun run, IEnumerable<WorkflowRunStep> records)
        {
            _root = new JsonObject
            {
                ["workflow"] = new JsonObject { ["id"] = run.WorkflowId, ["name"] = run.WorkflowName, ["version"] = run.WorkflowVersion },
                ["run"] = new JsonObject { ["id"] = run.Id, ["startedAt"] = run.StartedAt },
                ["trigger"] = JsonNode.Parse(run.TriggerJson),
                ["input"] = JsonNode.Parse(run.InputJson),
                ["steps"] = _steps,
            };
            foreach (var record in records.Where(r => r.Status is WorkflowStepStatus.Succeeded or WorkflowStepStatus.Failed or WorkflowStepStatus.Skipped))
            {
                Set(record.StepKey, record.Status, record.OutputJson is null ? null : JsonNode.Parse(record.OutputJson), record.Error);
            }
        }

        public void Record(string key, StepOutcome outcome)
            => Set(key, outcome.Status, outcome.Output is { } output ? JsonNode.Parse(output.GetRawText()) : null, outcome.Error);

        public JsonElement ToElement() => JsonSerializer.SerializeToElement(_root, PlatformJson.Options);

        private void Set(string key, WorkflowStepStatus status, JsonNode? output, string? error)
            => _steps[key] = new JsonObject
            {
                ["status"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(status.ToString()),
                ["output"] = output,
                ["error"] = error,
            };
    }
}

/// <summary>
/// 运行无法继续（例如所有者已离开空间），直接以该说明结束运行。
/// </summary>
internal sealed class WorkflowRunException(string message) : Exception(message);
