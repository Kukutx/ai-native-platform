using AiNative.Application.Abstractions;
using AiNative.Application.Actions;
using AiNative.Application.Ai;
using AiNative.Application.Common;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Application.Work;
using AiNative.Contracts.Agents;
using AiNative.Contracts.Realtime;
using AiNative.Domain.Agents;
using AiNative.Domain.Common;
using AiNative.Domain.Governance;
using AiNative.Domain.Work;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiNative.Application.Agents.Runtime;

/// <summary>
/// 执行一轮心跳：领取聚焦任务 → 组装上下文 → 与模型多轮往返并执行工具调用 → 记账、收尾。
/// 调度器已经把运行记录置为 Running 并保证同一智能体不会并发执行。
/// </summary>
public interface IAgentRuntime
{
    Task ExecuteAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken);
}

internal sealed class AgentRuntime(
    IAppDbContext db,
    TenantContext tenant,
    IModelGateway models,
    IActionInvoker actions,
    ITenantScopeFactory scopes,
    IWorkItemCheckout checkout,
    IRealtimeNotifier notifier,
    AgentPromptBuilder prompts,
    IOptions<AgentRunOptions> options,
    TimeProvider clock,
    ILogger<AgentRuntime> logger) : IAgentRuntime
{
    private readonly AgentRunOptions _options = options.Value;

    public async Task ExecuteAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken)
    {
        tenant.Enter(tenantId, Actor.System, []);
        var run = await db.HeartbeatRuns.FirstAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
        var agent = await db.Agents.FirstAsync(a => a.Id == run.AgentId, cancellationToken).ConfigureAwait(false);
        var wakeup = run.WakeupRequestId is { } wakeupId
            ? await db.WakeupRequests.FirstOrDefaultAsync(w => w.Id == wakeupId, cancellationToken).ConfigureAwait(false)
            : null;
        var recorder = new RunRecorder(db, notifier, run, logger);

        // 以智能体本人的身份与权限运行；所有工具调用的权限检查都基于这里。
        tenant.Enter(tenantId, Actor.ForAgent(agent.Id), agent.Permissions);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RunTimeout);
        var outcome = RunStatus.Succeeded;
        string? error = null;
        var budgetExhausted = false;
        try
        {
            // 领取到开始执行之间，智能体可能刚被暂停或终止。
            if (!agent.CanWork)
            {
                throw new OperationCanceledException("智能体已暂停或终止。");
            }

            run.Status = RunStatus.Running;
            run.StartedAt ??= clock.GetUtcNow();
            run.Provider = agent.ModelProvider;
            run.Model = agent.Model;
            agent.Status = AgentStatus.Running;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await PublishChangesAsync(tenantId, run, agent).ConfigureAwait(false);
            await recorder.RecordAsync(RunEventType.Status, new { status = "running", reasons = wakeup?.Items.Select(i => i.Reason) }, cancellationToken).ConfigureAwait(false);

            var spentAtStart = await AgentSpend.ForAgentAsync(db, agent.Id, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            if (agent.MonthlyBudgetUsd > 0 && spentAtStart >= agent.MonthlyBudgetUsd)
            {
                throw new BudgetExhaustedException();
            }

            var focus = await CheckoutFocusAsync(agent, run, wakeup, cancellationToken).ConfigureAwait(false);
            await LoopAsync(agent, run, wakeup, focus, recorder, spentAtStart, timeout.Token).ConfigureAwait(false);
        }
        catch (BudgetExhaustedException)
        {
            budgetExhausted = true;
            outcome = RunStatus.Failed;
            error = "本月预算已用完，智能体已自动暂停。调整预算后恢复即可继续。";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            outcome = RunStatus.TimedOut;
            error = $"执行超过 {_options.RunTimeout.TotalMinutes:0} 分钟上限。";
        }
        catch (OperationCanceledException ex)
        {
            outcome = RunStatus.Cancelled;
            error = cancellationToken.IsCancellationRequested ? "服务停止，执行被取消。" : ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent run {RunId} failed", runId);
            outcome = RunStatus.Failed;
            error = ex.Message;
        }

        await FinishAsync(tenantId, agent, run, wakeup, recorder, outcome, error, budgetExhausted).ConfigureAwait(false);
    }

    private async Task<WorkItem?> CheckoutFocusAsync(Agent agent, HeartbeatRun run, WakeupRequest? wakeup, CancellationToken cancellationToken)
    {
        // 最近一条带任务的唤醒原因决定本轮聚焦。自己负责的任务必须领取成功才聚焦（被别的执行持有就只看待办）；
        // 别人负责的任务（例如同事请你看一眼）只作为只读上下文提供。
        var candidate = wakeup?.Items.LastOrDefault(i => i.WorkItemId is not null)?.WorkItemId;
        if (candidate is not { } workItemId)
        {
            return null;
        }

        if (!await checkout.TryCheckoutAsync(workItemId, agent.Id, run.Id, cancellationToken).ConfigureAwait(false))
        {
            return await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId && w.AssigneeAgentId != agent.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        run.WorkItemId = workItemId;
        return await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId, cancellationToken).ConfigureAwait(false);
    }

    private async Task LoopAsync(
        Agent agent,
        HeartbeatRun run,
        WakeupRequest? wakeup,
        WorkItem? focus,
        RunRecorder recorder,
        decimal spentAtStart,
        CancellationToken cancellationToken)
    {
        var client = models.GetClient(agent.ModelProvider, agent.Model);
        var toolset = await ActionToolset.CreateAsync(actions, cancellationToken).ConfigureAwait(false);
        List<ChatMessage> messages =
        [
            new(ChatRole.System, await prompts.BuildSystemPromptAsync(agent, cancellationToken).ConfigureAwait(false)),
            new(ChatRole.User, await prompts.BuildWakeMessageAsync(agent, wakeup, focus, focus is not null && run.WorkItemId == focus.Id, cancellationToken).ConfigureAwait(false)),
        ];
        var chatOptions = new ChatOptions
        {
            ModelId = agent.Model,
            MaxOutputTokens = _options.MaxOutputTokens,
            Tools = toolset.Tools,
            ToolMode = ChatToolMode.Auto,
        };

        string? lastText = null;
        for (var step = 1; step <= _options.MaxStepsPerRun; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await StreamStepAsync(client, messages, chatOptions, recorder, cancellationToken).ConfigureAwait(false);
            run.Steps = step;
            await RecordCostAsync(agent, run, response.Usage, cancellationToken).ConfigureAwait(false);
            messages.AddRange(response.Messages);

            var text = response.Text?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                lastText = text;
                await recorder.RecordAsync(RunEventType.AssistantText, new { text }, cancellationToken).ConfigureAwait(false);
            }

            if (response.FinishReason == ChatFinishReason.ContentFilter)
            {
                throw new InvalidOperationException("模型拒绝了本轮请求。");
            }

            var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
            if (calls.Count == 0)
            {
                break;
            }

            var results = new List<AIContent>(calls.Count);
            foreach (var call in calls)
            {
                results.Add(await InvokeToolAsync(agent, run, toolset, call, text, recorder, cancellationToken).ConfigureAwait(false));
            }

            messages.Add(new ChatMessage(ChatRole.Tool, results));

            if (agent.MonthlyBudgetUsd > 0 && spentAtStart + run.CostUsd >= agent.MonthlyBudgetUsd)
            {
                throw new BudgetExhaustedException();
            }

            if (step == _options.MaxStepsPerRun)
            {
                lastText = (lastText is null ? string.Empty : lastText + "\n") + $"（已达到单轮 {step} 步上限，剩余工作留到下一轮。）";
            }
        }

        run.Summary = lastText is null ? "（本轮没有输出总结）" : Truncate(lastText, 4000);
    }

    private async Task<ChatResponse> StreamStepAsync(
        IChatClient client,
        List<ChatMessage> messages,
        ChatOptions chatOptions,
        RunRecorder recorder,
        CancellationToken cancellationToken)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
            var delta = update.Text;
            if (!string.IsNullOrEmpty(delta))
            {
                await recorder.LiveDeltaAsync(delta).ConfigureAwait(false);
            }
        }

        return updates.ToChatResponse();
    }

    private async Task<AIContent> InvokeToolAsync(
        Agent agent,
        HeartbeatRun run,
        ActionToolset toolset,
        FunctionCallContent call,
        string? rationale,
        RunRecorder recorder,
        CancellationToken cancellationToken)
    {
        var action = toolset.Resolve(call.Name);
        var actionKey = action?.Key ?? call.Name;
        await recorder.RecordAsync(RunEventType.ToolCall, new { callId = call.CallId, actionKey, arguments = call.Arguments }, cancellationToken)
            .ConfigureAwait(false);

        ActionInvocationResult result;
        if (action is null)
        {
            result = new ActionInvocationResult(ActionInvocationStatus.NotFound, Error: $"没有名为 {call.Name} 的工具。");
        }
        else
        {
            try
            {
                result = await ActionToolset.InvokeAsync(
                    scopes,
                    agent.TenantId,
                    Actor.ForAgent(agent.Id),
                    agent.Permissions,
                    action.Key,
                    call.Arguments,
                    new ActionInvocationOptions
                    {
                        RunId = run.Id,
                        WorkItemId = run.WorkItemId,
                        Rationale = rationale is null ? null : Truncate(rationale, 2000),
                        ExtraApprovalRequired = agent.ApprovalRequiredActions,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Tool {ActionKey} crashed in run {RunId}", action.Key, run.Id);
                result = new ActionInvocationResult(ActionInvocationStatus.Failed, Error: "工具执行出现内部错误。");
            }
        }

        await recorder.RecordAsync(
            result.Status == ActionInvocationStatus.PendingApproval ? RunEventType.ApprovalRequested : RunEventType.ToolResult,
            new
            {
                callId = call.CallId,
                actionKey,
                status = result.Status,
                output = result.Output,
                error = result.Error,
                approvalId = result.ApprovalId,
            },
            cancellationToken).ConfigureAwait(false);

        return new FunctionResultContent(call.CallId, ActionToolset.FormatForModel(result));
    }

    private async Task RecordCostAsync(Agent agent, HeartbeatRun run, UsageDetails? usage, CancellationToken cancellationToken)
    {
        var input = usage?.InputTokenCount ?? 0;
        var output = usage?.OutputTokenCount ?? 0;
        var cost = models.EstimateCostUsd(agent.ModelProvider, agent.Model, input, output);
        run.InputTokens += input;
        run.OutputTokens += output;
        run.CostUsd += cost;
        db.CostEvents.Add(new CostEvent
        {
            Actor = Actor.ForAgent(agent.Id),
            RunId = run.Id,
            Provider = agent.ModelProvider,
            Model = agent.Model,
            InputTokens = input,
            OutputTokens = output,
            CostUsd = cost,
            OccurredAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishAsync(
        Guid tenantId,
        Agent agent,
        HeartbeatRun run,
        WakeupRequest? wakeup,
        RunRecorder recorder,
        RunStatus outcome,
        string? error,
        bool budgetExhausted)
    {
        // 收尾不受调用方取消影响，保证状态一定落库、领取锁一定释放。
        var ct = CancellationToken.None;
        var now = clock.GetUtcNow();
        run.Status = outcome;
        run.Error = error;
        run.FinishedAt = now;

        // 执行期间管理员可能暂停或终止了智能体，先取最新状态，不能用本轮开始时的快照覆盖。
        try
        {
            await db.Entry(agent).ReloadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to reload agent {AgentId} before finishing run {RunId}", agent.Id, run.Id);
        }

        agent.LastHeartbeatAt = now;
        agent.LastError = outcome == RunStatus.Succeeded ? null : error;
        if (budgetExhausted && agent.CanWork)
        {
            agent.Pause("本月预算已用完");
        }
        else if (agent.Status == AgentStatus.Running)
        {
            agent.Status = outcome == RunStatus.Failed ? AgentStatus.Error : AgentStatus.Idle;
        }

        if (wakeup is not null)
        {
            wakeup.Status = outcome == RunStatus.Succeeded ? WakeupStatus.Completed : WakeupStatus.Failed;
            wakeup.FinishedAt = now;
        }

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await checkout.ReleaseAsync(run.Id, ct).ConfigureAwait(false);
            await recorder.RecordAsync(
                outcome == RunStatus.Succeeded ? RunEventType.Status : RunEventType.Error,
                new { status = outcome, summary = run.Summary, error },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to finalize run {RunId}", run.Id);
        }

        await PublishChangesAsync(tenantId, run, agent).ConfigureAwait(false);
    }

    private async Task PublishChangesAsync(Guid tenantId, HeartbeatRun run, Agent agent)
    {
        try
        {
            await notifier.ChangedAsync(tenantId, new RealtimeChangeDto(RealtimeEntities.Run, run.Id, RealtimeChangeKind.Updated)).ConfigureAwait(false);
            await notifier.ChangedAsync(tenantId, new RealtimeChangeDto(RealtimeEntities.Agent, agent.Id, RealtimeChangeKind.Updated)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Realtime publish failed for run {RunId}", run.Id);
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private sealed class BudgetExhaustedException : Exception;
}

/// <summary>
/// 运行事件记录：落库并实时推送。流式文本增量只推送不落库（Seq 为 0），整段文本在该步结束时落库。
/// </summary>
internal sealed class RunRecorder(IAppDbContext db, IRealtimeNotifier notifier, HeartbeatRun run, ILogger logger)
{
    private int _seq;

    public async Task RecordAsync(RunEventType type, object data, CancellationToken cancellationToken)
    {
        var json = PlatformJson.Serialize(data);
        var entity = new RunEvent { RunId = run.Id, Seq = ++_seq, Type = type, DataJson = json };
        db.RunEvents.Add(entity);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await PublishAsync(new RunEventDto(run.Id, run.AgentId, entity.Seq, type, PlatformJson.Parse(json), entity.CreatedAt)).ConfigureAwait(false);
    }

    public Task LiveDeltaAsync(string delta)
        => PublishAsync(new RunEventDto(run.Id, run.AgentId, 0, RunEventType.AssistantText, PlatformJson.ToElement(new { delta }), DateTimeOffset.UtcNow));

    private async Task PublishAsync(RunEventDto dto)
    {
        try
        {
            await notifier.RunEventAsync(run.TenantId, dto).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Realtime run event publish failed for run {RunId}", run.Id);
        }
    }
}
