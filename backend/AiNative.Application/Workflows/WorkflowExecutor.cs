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
/// 崩溃后重做未提交的步骤不会重复产生副作用。HTTP 请求无法回滚，崩溃时可能重发。</item>
/// <item>所有进度写入都以"运行仍是 Running 且停在这一顶层步骤"为条件；运行被取消或删除时条件不成立，整个步骤回滚。</item>
/// <item>需要等待（审批、智能体完成任务、延时）时把运行置为 Waiting 并结束本次执行，条件满足后由事件或引擎重新调度。</item>
/// <item>分支和遍历里的步骤各有一条按路径区分的记录，只有顶层步骤完成才推进 CurrentStep。重新执行时已完成的子步骤按记录重放，
/// 分支的选择和遍历的列表在开始时记进控制步骤的输入，之后不再重新求值。</item>
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
    IWorkflowHttpClient http,
    IOptions<WorkflowOptions> options,
    TimeProvider clock,
    ILogger<WorkflowExecutor> logger) : IWorkflowExecutor
{
    private const int DefaultHttpTimeoutSeconds = 30;

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
            var state = new RunState(run, principal, records);
            for (var index = run.CurrentStep; index < run.Steps.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outcome = await ExecuteStepAsync(state, run.Steps[index], StepPosition.ForTop(index), cancellationToken).ConfigureAwait(false);
                if (outcome.Kind != OutcomeKind.Completed)
                {
                    return;
                }
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

    /// <summary>
    /// 执行一个步骤（顶层或嵌套），完成后把结果写进上下文供后续步骤引用。
    /// </summary>
    private async Task<StepOutcome> ExecuteStepAsync(RunState state, WorkflowStep step, StepPosition position, CancellationToken cancellationToken)
    {
        var outcome = await RunStepAsync(state, step, position, cancellationToken).ConfigureAwait(false);
        if (outcome.Kind == OutcomeKind.Completed)
        {
            state.Context.Record(step.Key, outcome);
        }

        return outcome;
    }

    private async Task<StepOutcome> RunStepAsync(RunState state, WorkflowStep step, StepPosition position, CancellationToken cancellationToken)
    {
        if (state.Find(position.Path) is { } record)
        {
            switch (record.Status)
            {
                // 同一顶层步骤里已经完成的子步骤（之前的执行在等待或中断前完成的）：按记录重放，不再执行。
                case WorkflowStepStatus.Succeeded or WorkflowStepStatus.Failed or WorkflowStepStatus.Skipped:
                    return new StepOutcome(
                        OutcomeKind.Completed,
                        record.Status,
                        record.OutputJson is null ? null : PlatformJson.Parse(record.OutputJson),
                        record.Error);
                case WorkflowStepStatus.Waiting:
                    return await ResumeStepAsync(state.Run, step, position, cancellationToken).ConfigureAwait(false);
                case WorkflowStepStatus.Running when step.Kind == WorkflowStepKind.Branch && ChosenBranch(record) is { } chosen:
                    return await RunBranchAsync(state, step, position, chosen, cancellationToken).ConfigureAwait(false);
                case WorkflowStepStatus.Running when step.Kind == WorkflowStepKind.ForEach && LoopItems(record) is { } items:
                    return await RunForEachAsync(state, step, position, items, cancellationToken).ConfigureAwait(false);
            }
        }

        // 没有记录，或是没提交完的叶子步骤（崩溃时副作用已随事务回滚）：从头执行。
        return await StartStepAsync(state, step, position, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StepOutcome> StartStepAsync(RunState state, WorkflowStep step, StepPosition position, CancellationToken cancellationToken)
    {
        var run = state.Run;
        var principal = state.Principal;
        var context = state.Context.ToElement();
        try
        {
            if (step.When is { } when && !WorkflowExpression.Parse(when).IsTrue(context))
            {
                return await InUnitAsync(() => CompleteInAsync(db, changes, run, step, position, WorkflowStepStatus.Skipped, null, null, null, cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
            }

            switch (step.Kind)
            {
                case WorkflowStepKind.Action:
                    var input = step.Input is { } template ? WorkflowTemplate.Render(template, context) : PlatformJson.ToElement(new { });
                    return await RunActionAsync(run, principal, step, position, input, cancellationToken).ConfigureAwait(false);

                case WorkflowStepKind.AgentTask:
                    var title = WorkflowTemplate.RenderString(step.Title, context);
                    var instructions = WorkflowTemplate.RenderString(step.Instructions, context);
                    return await RunAgentTaskAsync(run, principal, step, position, title, instructions, cancellationToken).ConfigureAwait(false);

                case WorkflowStepKind.Llm:
                    var prompt = WorkflowTemplate.RenderString(step.Prompt, context);
                    return await RunLlmAsync(run, principal, step, position, prompt, cancellationToken).ConfigureAwait(false);

                case WorkflowStepKind.Delay:
                    var seconds = step.DelaySeconds ?? 0;
                    var delayInput = PlatformJson.ToElement(new { seconds });
                    return await InUnitAsync(async () =>
                    {
                        await WaitInAsync(db, changes, run, step, position, delayInput, null, null, clock.GetUtcNow().AddSeconds(seconds), cancellationToken).ConfigureAwait(false);
                        return StepOutcome.Waiting;
                    }, cancellationToken).ConfigureAwait(false);

                case WorkflowStepKind.Http:
                    return await RunHttpAsync(run, step, position, context, cancellationToken).ConfigureAwait(false);

                case WorkflowStepKind.Branch:
                    return await StartBranchAsync(state, step, position, context, cancellationToken).ConfigureAwait(false);

                case WorkflowStepKind.ForEach:
                    return await StartForEachAsync(state, step, position, context, cancellationToken).ConfigureAwait(false);

                default:
                    return await FailStepAsync(run, step, position, null, $"不支持的步骤类型 {step.Kind}。", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (WorkflowExpressionException ex)
        {
            return await FailStepAsync(run, step, position, null, $"表达式错误：{ex.Message}", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 按顺序执行一组子步骤（某个分支，或遍历的某一项）。遇到等待或停止时中断，全部完成时给出最后一个实际执行的步骤的输出。
    /// </summary>
    private async Task<SequenceResult> RunSequenceAsync(RunState state, IReadOnlyList<WorkflowStep> steps, StepPosition parent, int group, CancellationToken cancellationToken)
    {
        JsonElement? last = null;
        for (var i = 0; i < steps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await ExecuteStepAsync(state, steps[i], parent.Child(group, i), cancellationToken).ConfigureAwait(false);
            if (outcome.Kind != OutcomeKind.Completed)
            {
                return new SequenceResult(outcome, null);
            }

            if (outcome.Status != WorkflowStepStatus.Skipped)
            {
                last = outcome.Output;
            }
        }

        return new SequenceResult(null, last);
    }

    private async Task<StepOutcome> StartBranchAsync(RunState state, WorkflowStep step, StepPosition position, JsonElement context, CancellationToken cancellationToken)
    {
        var branches = step.Branches ?? [];
        var chosen = -1;
        for (var i = 0; i < branches.Count; i++)
        {
            if (branches[i].When is not { } condition || WorkflowExpression.Parse(condition).IsTrue(context))
            {
                chosen = i;
                break;
            }
        }

        if (chosen < 0)
        {
            var none = PlatformJson.ToElement(new { index = -1 });
            return await InUnitAsync(() => CompleteInAsync(db, changes, state.Run, step, position, WorkflowStepStatus.Succeeded, null, none, null, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }

        var input = PlatformJson.ToElement(new { branch = chosen, name = branches[chosen].Name });
        if (!await MarkRunningAsync(state.Run, step, position, input, cancellationToken).ConfigureAwait(false))
        {
            return StepOutcome.Stopped;
        }

        return await RunBranchAsync(state, step, position, chosen, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StepOutcome> RunBranchAsync(RunState state, WorkflowStep step, StepPosition position, int chosen, CancellationToken cancellationToken)
    {
        var branch = step.Branches![chosen];
        var sequence = await RunSequenceAsync(state, branch.Steps, position, chosen, cancellationToken).ConfigureAwait(false);
        if (sequence.Interruption is { } interruption)
        {
            return interruption;
        }

        var output = PlatformJson.ToElement(new { index = chosen, branch = branch.Name });
        return await InUnitAsync(() => CompleteInAsync(db, changes, state.Run, step, position, WorkflowStepStatus.Succeeded, null, output, null, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<StepOutcome> StartForEachAsync(RunState state, WorkflowStep step, StepPosition position, JsonElement context, CancellationToken cancellationToken)
    {
        JsonElement items;
        switch (Values.Normalize(WorkflowExpression.Parse(step.Items!).Evaluate(context)))
        {
            case null:
                items = PlatformJson.ToElement(Array.Empty<object>());
                break;
            case JsonElement { ValueKind: JsonValueKind.Array } array:
                items = array.Clone();
                break;
            default:
                return await FailStepAsync(state.Run, step, position, null, $"「{step.Items}」的值不是列表，无法遍历。", cancellationToken).ConfigureAwait(false);
        }

        var count = items.GetArrayLength();
        if (count > _options.MaxLoopItems)
        {
            return await FailStepAsync(state.Run, step, position, PlatformJson.ToElement(new { count }), $"列表有 {count} 项，超过单次遍历上限 {_options.MaxLoopItems} 项。", cancellationToken)
                .ConfigureAwait(false);
        }

        // 列表快照记进输入：等待之后继续遍历时用的是开始时的列表，不受之后数据变化影响。
        if (!await MarkRunningAsync(state.Run, step, position, PlatformJson.ToElement(new { count, items }), cancellationToken).ConfigureAwait(false))
        {
            return StepOutcome.Stopped;
        }

        return await RunForEachAsync(state, step, position, items, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StepOutcome> RunForEachAsync(RunState state, WorkflowStep step, StepPosition position, JsonElement items, CancellationToken cancellationToken)
    {
        var body = step.Steps ?? [];
        var bodyKeys = body.SelectMany(s => s.SelfAndDescendants()).Select(s => s.Key).ToList();
        var results = new List<JsonElement?>(items.GetArrayLength());
        var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            // 每一项开始时清掉上一项的子步骤结果，条件里引用的总是本项的结果。
            state.Context.Forget(bodyKeys);
            using (state.Context.EnterLoop(item, index))
            {
                var sequence = await RunSequenceAsync(state, body, position, index, cancellationToken).ConfigureAwait(false);
                if (sequence.Interruption is { } interruption)
                {
                    return interruption;
                }

                results.Add(sequence.LastOutput);
            }

            index++;
        }

        var output = PlatformJson.ToElement(new { count = results.Count, results });
        return await InUnitAsync(() => CompleteInAsync(db, changes, state.Run, step, position, WorkflowStepStatus.Succeeded, null, output, null, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private static int? ChosenBranch(WorkflowRunStep record)
        => record.InputJson is { } json && PlatformJson.Parse(json) is { ValueKind: JsonValueKind.Object } input
           && input.TryGetProperty("branch", out var branch) && branch.TryGetInt32(out var chosen) && chosen >= 0
            ? chosen
            : null;

    private static JsonElement? LoopItems(WorkflowRunStep record)
        => record.InputJson is { } json && PlatformJson.Parse(json) is { ValueKind: JsonValueKind.Object } input
           && input.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.Clone()
            : null;

    private async Task<StepOutcome> RunHttpAsync(WorkflowRun run, WorkflowStep step, StepPosition position, JsonElement context, CancellationToken cancellationToken)
    {
        var method = step.Method ?? "GET";
        var url = WorkflowTemplate.RenderString(step.Url, context).Trim();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in step.Headers ?? [])
        {
            headers[name] = WorkflowTemplate.RenderString(value, context);
        }

        JsonElement? body = step.Body is { } template ? WorkflowTemplate.Render(template, context) : null;
        var timeout = TimeSpan.FromSeconds(step.TimeoutSeconds ?? DefaultHttpTimeoutSeconds);

        // 记录里的敏感请求头打码：运行详情对所有能查看工作流的人可见。
        var recorded = PlatformJson.ToElement(new { method, url, headers = headers.Count == 0 ? null : headers.ToDictionary(h => h.Key, h => IsSensitiveHeader(h.Key) ? "***" : h.Value), body });
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return await FailStepAsync(run, step, position, recorded, $"「{url}」不是有效的 http(s) 地址。", cancellationToken).ConfigureAwait(false);
        }

        if (!await MarkRunningAsync(run, step, position, recorded, cancellationToken).ConfigureAwait(false))
        {
            return StepOutcome.Stopped;
        }

        WorkflowHttpResponse response;
        try
        {
            response = await http.SendAsync(new WorkflowHttpRequest(method, uri, headers, body, timeout), cancellationToken).ConfigureAwait(false);
        }
        catch (WorkflowHttpException ex)
        {
            return await FailStepAsync(run, step, position, null, ex.Message, cancellationToken).ConfigureAwait(false);
        }

        var output = PlatformJson.ToElement(new { status = response.Status, ok = response.IsSuccess, contentType = response.ContentType, body = HttpBody(response) });
        var status = response.IsSuccess ? WorkflowStepStatus.Succeeded : WorkflowStepStatus.Failed;
        var error = response.IsSuccess ? null : $"服务器返回 HTTP {response.Status}。";
        return await InUnitAsync(() => CompleteInAsync(db, changes, run, step, position, status, null, output, error, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsSensitiveHeader(string name)
        => name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
           || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
           || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
           || name.Contains("token", StringComparison.OrdinalIgnoreCase)
           || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
           || name.Contains("key", StringComparison.OrdinalIgnoreCase)
           || name.Contains("password", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 响应是 JSON 时解析成对象，后续步骤可以按字段引用；否则给原文。
    /// </summary>
    private static object? HttpBody(WorkflowHttpResponse response)
    {
        if (response.Body.Length == 0)
        {
            return null;
        }

        if (response.ContentType is { } type && type.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var document = JsonDocument.Parse(response.Body);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
            }
        }

        return response.Body;
    }

    private async Task<StepOutcome> RunActionAsync(WorkflowRun run, WorkflowPrincipal principal, WorkflowStep step, StepPosition position, JsonElement input, CancellationToken cancellationToken)
    {
        if (!await MarkRunningAsync(run, step, position, input, cancellationToken).ConfigureAwait(false))
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
                            return await CompleteInAsync(stepDb, feed, run, step, position, WorkflowStepStatus.Succeeded, null, result.Output ?? PlatformJson.ToElement<object?>(null), null, cancellationToken)
                                .ConfigureAwait(false);
                        case ActionInvocationStatus.PendingApproval:
                            await WaitInAsync(stepDb, feed, run, step, position, null, result.ApprovalId, null, null, cancellationToken).ConfigureAwait(false);
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

        return await FailStepAsync(run, step, position, null, error, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StepOutcome> RunAgentTaskAsync(
        WorkflowRun run,
        WorkflowPrincipal principal,
        WorkflowStep step,
        StepPosition position,
        string title,
        string instructions,
        CancellationToken cancellationToken)
    {
        var input = PlatformJson.ToElement(new { agentId = step.AgentId, title, instructions, step.WaitForCompletion, step.TimeoutMinutes });
        if (!await MarkRunningAsync(run, step, position, input, cancellationToken).ConfigureAwait(false))
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
                        return await CompleteInAsync(stepDb, feed, run, step, position, WorkflowStepStatus.Succeeded, null, created, null, cancellationToken).ConfigureAwait(false);
                    }

                    var deadline = step.TimeoutMinutes is { } minutes ? clock.GetUtcNow().AddMinutes(minutes) : (DateTimeOffset?)null;
                    await WaitInAsync(stepDb, feed, run, step, position, null, null, task.Id, deadline, cancellationToken).ConfigureAwait(false);
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

        return await FailStepAsync(run, step, position, null, error, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StepOutcome> RunLlmAsync(WorkflowRun run, WorkflowPrincipal principal, WorkflowStep step, StepPosition position, string prompt, CancellationToken cancellationToken)
    {
        if (!await MarkRunningAsync(run, step, position, PlatformJson.ToElement(new { prompt }), cancellationToken).ConfigureAwait(false))
        {
            return StepOutcome.Stopped;
        }

        // 智能体所有的工作流用该智能体的模型、计入它的预算；成员所有的用平台默认模型。
        var (provider, model) = principal.Agent is { } owner ? (owner.ModelProvider, owner.Model) : (models.Default.Provider, models.Default.Model);
        if (principal.Agent is { MonthlyBudgetUsd: > 0 } budgeted
            && await AgentSpend.ForAgentAsync(db, budgeted.Id, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false) >= budgeted.MonthlyBudgetUsd)
        {
            return await FailStepAsync(run, step, position, null, "所属智能体本月预算已用完。", cancellationToken).ConfigureAwait(false);
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
                return await FailStepAsync(run, step, position, null, $"模型调用超过 {_options.LlmTimeout.TotalSeconds:0} 秒未返回。", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Workflow run {RunId} model step {Step} failed", run.Id, step.Key);
                return await FailStepAsync(run, step, position, null, $"模型调用失败：{ex.Message}", cancellationToken).ConfigureAwait(false);
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
            return CompleteInAsync(db, changes, run, step, position, WorkflowStepStatus.Succeeded, null, LlmOutput(text), null, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 被重新调度的等待中步骤：检查等待的条件是否已经满足。
    /// </summary>
    private async Task<StepOutcome> ResumeStepAsync(WorkflowRun run, WorkflowStep step, StepPosition position, CancellationToken cancellationToken)
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
                return await FailStepAsync(run, step, position, null, "等待的对象已不存在。", cancellationToken).ConfigureAwait(false);
        }

        var outcome = await InUnitAsync(async () =>
        {
            if (subject is { } id)
            {
                await locks.AcquireAsync(WorkflowTriggers.WaitLockKey(id), cancellationToken).ConfigureAwait(false);
            }

            if (await check().ConfigureAwait(false) is { } resolution)
            {
                return await CompleteInAsync(db, changes, run, step, position, resolution.Status, null, resolution.Output, resolution.Error, cancellationToken).ConfigureAwait(false);
            }

            if (!await TransitionAsync(db, run, position.Top, s => s.SetProperty(r => r.Status, WorkflowRunStatus.Waiting), cancellationToken).ConfigureAwait(false))
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

    private async Task<bool> MarkRunningAsync(WorkflowRun run, WorkflowStep step, StepPosition position, JsonElement input, CancellationToken cancellationToken)
    {
        var outcome = await InUnitAsync(async () =>
        {
            if (!await TransitionAsync(db, run, position.Top, _ => { }, cancellationToken).ConfigureAwait(false))
            {
                throw new RunStoppedException();
            }

            await UpsertStepAsync(db, run, step, position, WorkflowStepStatus.Running, input, null, null, finished: false, cancellationToken).ConfigureAwait(false);
            changes.Publish(RealtimeEntities.WorkflowRun, run.Id, RealtimeChangeKind.Updated);
            return new StepOutcome(OutcomeKind.Completed, WorkflowStepStatus.Running);
        }, cancellationToken).ConfigureAwait(false);
        return outcome.Kind != OutcomeKind.Stopped;
    }

    private async Task<StepOutcome> FailStepAsync(WorkflowRun run, WorkflowStep step, StepPosition position, JsonElement? input, string error, CancellationToken cancellationToken)
        => await InUnitAsync(() => CompleteInAsync(db, changes, run, step, position, WorkflowStepStatus.Failed, input, null, error, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// 在当前事务里记录一步的结果，顶层步骤同时推进进度；失败且不允许继续时直接把整个运行标记为失败
    /// （同一事务，不会出现"步骤失败但运行继续"），所在的分支或遍历一并标记失败。
    /// </summary>
    private async Task<StepOutcome> CompleteInAsync(
        IAppDbContext stepDb,
        IChangeFeed feed,
        WorkflowRun run,
        WorkflowStep step,
        StepPosition position,
        WorkflowStepStatus status,
        JsonElement? input,
        JsonElement? output,
        string? error,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var stopsRun = status == WorkflowStepStatus.Failed && !step.ContinueOnError;
        var top = position.Top;
        var nextStep = position.IsTop ? top + 1 : top;
        var moved = stopsRun
            ? await TransitionAsync(stepDb, run, top, s => s
                .SetProperty(r => r.Status, WorkflowRunStatus.Failed)
                .SetProperty(r => r.Error, $"步骤「{step.Name}」失败：{error}")
                .SetProperty(r => r.ResumeAt, (DateTimeOffset?)null)
                .SetProperty(r => r.FinishedAt, now), cancellationToken).ConfigureAwait(false)
            : await TransitionAsync(stepDb, run, top, s => s
                .SetProperty(r => r.CurrentStep, nextStep)
                .SetProperty(r => r.WaitingApprovalId, (Guid?)null)
                .SetProperty(r => r.WaitingWorkItemId, (Guid?)null)
                .SetProperty(r => r.ResumeAt, (DateTimeOffset?)null), cancellationToken).ConfigureAwait(false);
        if (!moved)
        {
            throw new RunStoppedException();
        }

        await UpsertStepAsync(stepDb, run, step, position, status, input, output, error, finished: true, cancellationToken).ConfigureAwait(false);
        if (stopsRun && !position.IsTop)
        {
            var path = position.Path;
            await stepDb.WorkflowRunSteps
                .Where(s => s.RunId == run.Id && s.Index == top && s.Status == WorkflowStepStatus.Running && s.Path != path)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, WorkflowStepStatus.Failed)
                    .SetProperty(x => x.Error, $"子步骤「{step.Name}」失败。")
                    .SetProperty(x => x.FinishedAt, now)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken)
                .ConfigureAwait(false);
        }

        feed.Publish(RealtimeEntities.WorkflowRun, run.Id, RealtimeChangeKind.Updated);
        return stopsRun ? StepOutcome.Stopped : new StepOutcome(OutcomeKind.Completed, status, output, error);
    }

    private async Task WaitInAsync(
        IAppDbContext stepDb,
        IChangeFeed feed,
        WorkflowRun run,
        WorkflowStep step,
        StepPosition position,
        JsonElement? input,
        Guid? approvalId,
        Guid? workItemId,
        DateTimeOffset? resumeAt,
        CancellationToken cancellationToken)
    {
        var moved = await TransitionAsync(stepDb, run, position.Top, s => s
            .SetProperty(r => r.Status, WorkflowRunStatus.Waiting)
            .SetProperty(r => r.WaitingApprovalId, approvalId)
            .SetProperty(r => r.WaitingWorkItemId, workItemId)
            .SetProperty(r => r.ResumeAt, resumeAt), cancellationToken).ConfigureAwait(false);
        if (!moved)
        {
            throw new RunStoppedException();
        }

        await UpsertStepAsync(stepDb, run, step, position, WorkflowStepStatus.Waiting, input, null, null, finished: false, cancellationToken).ConfigureAwait(false);
        feed.Publish(RealtimeEntities.WorkflowRun, run.Id, RealtimeChangeKind.Updated);
    }

    /// <summary>
    /// 条件更新运行：只有仍在执行且停在这一顶层步骤时才生效。
    /// </summary>
    private async Task<bool> TransitionAsync(
        IAppDbContext stepDb,
        WorkflowRun run,
        int top,
        Action<UpdateSettersBuilder<WorkflowRun>> set,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var affected = await stepDb.WorkflowRuns
            .Where(r => r.Id == run.Id && r.Status == WorkflowRunStatus.Running && r.CurrentStep == top)
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
        StepPosition position,
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
        var path = position.Path;
        var exists = await stepDb.WorkflowRunSteps.AnyAsync(s => s.RunId == run.Id && s.Path == path, cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            stepDb.WorkflowRunSteps.Add(new WorkflowRunStep
            {
                RunId = run.Id,
                Index = position.Top,
                Path = path,
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
            .Where(s => s.RunId == run.Id && s.Path == path)
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

    /// <param name="Interruption">中途等待或停止时的结果；为 null 表示全部完成。</param>
    private sealed record SequenceResult(StepOutcome? Interruption, JsonElement? LastOutput);

    /// <summary>
    /// 一次执行的状态：运行、所有者身份、执行开始时已有的步骤记录（按路径）和表达式上下文。
    /// </summary>
    private sealed class RunState(WorkflowRun run, WorkflowPrincipal principal, IReadOnlyList<WorkflowRunStep> records)
    {
        private readonly Dictionary<string, WorkflowRunStep> _records = records.ToDictionary(r => r.Path, StringComparer.Ordinal);

        public WorkflowRun Run { get; } = run;

        public WorkflowPrincipal Principal { get; } = principal;

        public RunContext Context { get; } = new(run, records);

        public WorkflowRunStep? Find(string path) => _records.GetValueOrDefault(path);
    }

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
    /// 表达式求值用的运行上下文：workflow、run、trigger、input、已完成步骤的 steps.&lt;key&gt;，遍历中另有当前项 item 和序号 index。
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
            // 按执行顺序写入：遍历里的子步骤以最后一项的结果为准。
            foreach (var record in records
                         .Where(r => r.Status is WorkflowStepStatus.Succeeded or WorkflowStepStatus.Failed or WorkflowStepStatus.Skipped)
                         .OrderBy(r => r.Path, StepPathComparer.Instance))
            {
                Set(record.StepKey, record.Status, record.OutputJson is null ? null : JsonNode.Parse(record.OutputJson), record.Error);
            }
        }

        public void Record(string key, StepOutcome outcome)
            => Set(key, outcome.Status, outcome.Output is { } output ? JsonNode.Parse(output.GetRawText()) : null, outcome.Error);

        public JsonElement ToElement() => JsonSerializer.SerializeToElement(_root, PlatformJson.Options);

        public void Forget(IEnumerable<string> keys)
        {
            foreach (var key in keys)
            {
                _steps.Remove(key);
            }
        }

        /// <summary>
        /// 进入遍历的一项；释放时恢复外层遍历的 item 和 index（嵌套遍历时内层覆盖外层）。
        /// </summary>
        public IDisposable EnterLoop(JsonElement item, int index)
        {
            var previousItem = _root["item"]?.DeepClone();
            var previousIndex = _root["index"]?.DeepClone();
            var hadLoop = _root.ContainsKey("index");
            _root["item"] = JsonNode.Parse(item.GetRawText());
            _root["index"] = index;
            return new LoopScope(() =>
            {
                if (hadLoop)
                {
                    _root["item"] = previousItem;
                    _root["index"] = previousIndex;
                }
                else
                {
                    _root.Remove("item");
                    _root.Remove("index");
                }
            });
        }

        private void Set(string key, WorkflowStepStatus status, JsonNode? output, string? error)
            => _steps[key] = new JsonObject
            {
                ["status"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(status.ToString()),
                ["output"] = output,
                ["error"] = error,
            };

        private sealed class LoopScope(Action restore) : IDisposable
        {
            public void Dispose() => restore();
        }
    }
}

/// <summary>
/// 运行无法继续（例如所有者已离开空间），直接以该说明结束运行。
/// </summary>
internal sealed class WorkflowRunException(string message) : Exception(message);
