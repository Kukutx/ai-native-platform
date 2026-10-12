using System.Text.Json;
using System.Text.RegularExpressions;
using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Actions;
using AiNative.Application.Common;
using AiNative.Application.Modules;
using AiNative.Application.Tenancy;
using AiNative.Application.Workflows.Expressions;
using AiNative.Contracts.Common;
using AiNative.Contracts.Workflows;
using AiNative.Domain.Agents;
using AiNative.Domain.Common;
using AiNative.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Workflows;

internal static class WorkflowMapping
{
    public static WorkflowTriggerDto TriggerOf(Workflow workflow) => new()
    {
        Kind = workflow.TriggerKind,
        EventType = workflow.EventType,
        Filter = workflow.EventFilter,
        Cron = workflow.Cron,
        TimeZone = workflow.TimeZone,
    };

    public static WorkflowDto ToDto(this Workflow workflow, Func<Actor, ActorDto> actors, WorkflowRunSummaryDto? lastRun) => new(
        workflow.Id,
        workflow.Name,
        workflow.Description,
        workflow.IsEnabled,
        TriggerOf(workflow),
        workflow.Steps,
        actors(workflow.Owner),
        workflow.Version,
        workflow.NextRunAt,
        lastRun,
        workflow.CreatedAt,
        workflow.UpdatedAt);

    public static WorkflowRunSummaryDto ToSummary(this WorkflowRun run, Func<Actor, ActorDto> actors) => new(
        run.Id,
        run.WorkflowId,
        run.WorkflowName,
        run.Status,
        run.TriggerKind,
        run.EventType,
        actors(run.TriggeredBy),
        run.CurrentStep,
        run.Steps.Count,
        run.Error,
        run.CreatedAt,
        run.StartedAt,
        run.FinishedAt);

    public static WorkflowRunStepDto ToDto(this WorkflowRunStep step) => new(
        step.Index,
        step.StepKey,
        step.Name,
        step.Kind,
        step.Status,
        step.InputJson is null ? null : PlatformJson.Parse(step.InputJson),
        step.OutputJson is null ? null : PlatformJson.Parse(step.OutputJson),
        step.Error,
        step.StartedAt,
        step.FinishedAt);

    public static async Task<IReadOnlyList<WorkflowDto>> ToDtosAsync(IAppDbContext db, IReadOnlyList<Workflow> workflows, CancellationToken cancellationToken)
    {
        var ids = workflows.Select(w => w.Id).ToList();
        var lastRuns = ids.Count == 0
            ? []
            : await db.WorkflowRuns.AsNoTracking()
                .Where(r => ids.Contains(r.WorkflowId))
                .GroupBy(r => r.WorkflowId)
                .Select(g => g.OrderByDescending(r => r.CreatedAt).First())
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        var actors = await ActorNames.ResolveAsync(
            db,
            workflows.Select(w => w.Owner).Concat(lastRuns.Select(r => r.TriggeredBy)),
            cancellationToken).ConfigureAwait(false);
        var byWorkflow = lastRuns.ToDictionary(r => r.WorkflowId);
        return [.. workflows.Select(w => w.ToDto(actors, byWorkflow.TryGetValue(w.Id, out var run) ? run.ToSummary(actors) : null))];
    }
}

internal static class WorkflowRuns
{
    /// <summary>
    /// 新建一次运行：保存定义快照与运行身份；在另一个工作流的步骤里引发时记录来源与层数。
    /// </summary>
    public static WorkflowRun Create(
        Workflow workflow,
        WorkflowTriggerKind kind,
        string? eventType,
        Actor triggeredBy,
        JsonElement trigger,
        JsonElement input,
        WorkflowExecutionScope scope) => new()
        {
            WorkflowId = workflow.Id,
            WorkflowVersion = workflow.Version,
            WorkflowName = workflow.Name,
            Steps = [.. workflow.Steps],
            Owner = workflow.Owner,
            TriggeredBy = triggeredBy,
            TriggerKind = kind,
            EventType = eventType,
            TriggerJson = trigger.GetRawText(),
            InputJson = input.ValueKind == JsonValueKind.Object ? input.GetRawText() : "{}",
            ParentRunId = scope.RunId,
            Depth = scope.ChildDepth,
        };

    public static object ActorPayload(Actor actor, Func<Actor, ActorDto> names)
    {
        var dto = names(actor);
        return new { kind = dto.Kind, id = dto.Id, name = dto.Name };
    }
}

internal static class WorkflowSchedule
{
    /// <summary>
    /// 定时触发的下一次执行时间；未启用、不是定时触发或规则无效时为 null。
    /// </summary>
    public static DateTimeOffset? NextRun(Workflow workflow, DateTimeOffset after)
    {
        if (!workflow.IsEnabled || workflow.TriggerKind != WorkflowTriggerKind.Schedule || string.IsNullOrWhiteSpace(workflow.Cron)
            || CronSchedule.FindTimeZone(workflow.TimeZone) is not { } zone
            || !CronSchedule.TryParse(workflow.Cron, out var schedule, out _))
        {
            return null;
        }

        return schedule!.GetNextOccurrence(after, zone);
    }
}

/// <summary>
/// 校验并规整工作流定义：语法（表达式、cron、时区）、引用（动作、事件、智能体）、以及保存者是否有权限做这些事
/// （工作流以保存者身份运行，保存时就把越权的配置挡回去，而不是等运行时才失败）。
/// </summary>
internal sealed partial class WorkflowDefinitionValidator(
    IActionInvoker actions,
    IModuleCatalog catalog,
    IModuleAccess modules,
    IAppDbContext db,
    ITenantContext tenant,
    TimeProvider clock)
{
    public const int MaxSteps = 30;
    public const int MaxDelaySeconds = 30 * 24 * 3600;
    public const int MaxTimeoutMinutes = 30 * 24 * 60;

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex StepKeyPattern();

    public async Task<(WorkflowTriggerDto Trigger, List<WorkflowStep> Steps)> ValidateAsync(SaveWorkflowRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, List<string>>();
        void Add(string key, string message)
        {
            if (!errors.TryGetValue(key, out var list))
            {
                errors[key] = list = [];
            }

            list.Add(message);
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            Add("name", "名称不能为空。");
        }
        else if (request.Name.Trim().Length > 100)
        {
            Add("name", "名称不能超过 100 个字符。");
        }

        if ((request.Description ?? string.Empty).Length > 2000)
        {
            Add("description", "说明不能超过 2000 个字符。");
        }

        var trigger = await ValidateTriggerAsync(request.Trigger, Add, cancellationToken).ConfigureAwait(false);
        var steps = await ValidateStepsAsync(request.Steps ?? [], Add, cancellationToken).ConfigureAwait(false);

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
        }

        return (trigger, steps);
    }

    private async Task<WorkflowTriggerDto> ValidateTriggerAsync(WorkflowTriggerDto? trigger, Action<string, string> add, CancellationToken cancellationToken)
    {
        if (trigger is null)
        {
            add("trigger", "请选择触发方式。");
            return new WorkflowTriggerDto();
        }

        switch (trigger.Kind)
        {
            case WorkflowTriggerKind.Event:
                var eventType = trigger.EventType?.Trim();
                var definition = string.IsNullOrEmpty(eventType) ? null : catalog.FindEvent(eventType);
                if (definition is null)
                {
                    add("trigger.eventType", string.IsNullOrEmpty(eventType) ? "请选择触发事件。" : $"未知事件 {eventType}。");
                }
                else if (!await modules.IsEnabledAsync(definition.ModuleKey, cancellationToken).ConfigureAwait(false))
                {
                    add("trigger.eventType", $"事件所属模块 {definition.ModuleKey} 未启用。");
                }
                else if (!tenant.Can(definition.RequiredFeature))
                {
                    add("trigger.eventType", $"订阅「{definition.Name}」需要权限 {definition.RequiredFeature}。");
                }

                var filter = string.IsNullOrWhiteSpace(trigger.Filter) ? null : trigger.Filter.Trim();
                if (filter is not null && WorkflowExpression.Validate(filter) is { } filterError)
                {
                    add("trigger.filter", filterError);
                }

                return new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Event, EventType = eventType, Filter = filter };

            case WorkflowTriggerKind.Schedule:
                var cron = trigger.Cron?.Trim() ?? string.Empty;
                var timeZone = string.IsNullOrWhiteSpace(trigger.TimeZone) ? "UTC" : trigger.TimeZone.Trim();
                var zone = CronSchedule.FindTimeZone(timeZone);
                if (zone is null)
                {
                    add("trigger.timeZone", $"无法识别的时区 {timeZone}，请使用 IANA 名称，例如 Asia/Shanghai。");
                }

                if (!CronSchedule.TryParse(cron, out var schedule, out var cronError))
                {
                    add("trigger.cron", cronError!);
                }
                else if (zone is not null && schedule!.GetNextOccurrence(clock.GetUtcNow(), zone) is null)
                {
                    add("trigger.cron", "这个 cron 规则永远不会触发。");
                }

                return new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Schedule, Cron = schedule?.Expression ?? cron, TimeZone = timeZone };

            default:
                return new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Manual };
        }
    }

    private async Task<List<WorkflowStep>> ValidateStepsAsync(IReadOnlyList<WorkflowStep> steps, Action<string, string> add, CancellationToken cancellationToken)
    {
        if (steps.Count == 0)
        {
            add("steps", "至少需要一个步骤。");
        }
        else if (steps.Count > MaxSteps)
        {
            add("steps", $"步骤不能超过 {MaxSteps} 个。");
        }

        var agentIds = steps.Where(s => s.Kind == WorkflowStepKind.AgentTask && s.AgentId is not null).Select(s => s.AgentId!.Value).Distinct().ToList();
        var agents = agentIds.Count == 0
            ? new Dictionary<Guid, AgentStatus>()
            : await db.Agents.AsNoTracking().Where(a => agentIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.Status, cancellationToken).ConfigureAwait(false);
        var enabledModules = await modules.GetEnabledAsync(cancellationToken).ConfigureAwait(false);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<WorkflowStep>(steps.Count);
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var path = $"steps[{i}]";
            var key = step.Key?.Trim() ?? string.Empty;
            if (!StepKeyPattern().IsMatch(key))
            {
                add($"{path}.key", "步骤标识需以小写字母开头，只能包含小写字母、数字和下划线，最长 40 个字符。");
            }
            else if (!keys.Add(key))
            {
                add($"{path}.key", $"步骤标识 {key} 重复。");
            }

            var name = step.Name?.Trim() ?? string.Empty;
            if (name.Length is 0 or > 100)
            {
                add($"{path}.name", "步骤名称不能为空，且不超过 100 个字符。");
            }

            var when = string.IsNullOrWhiteSpace(step.When) ? null : step.When.Trim();
            if (when is not null && WorkflowExpression.Validate(when) is { } whenError)
            {
                add($"{path}.when", whenError);
            }

            void CheckTemplate(string field, string? text, int maxLength, bool required)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    if (required)
                    {
                        add($"{path}.{field}", "不能为空。");
                    }

                    return;
                }

                if (text.Length > maxLength)
                {
                    add($"{path}.{field}", $"不能超过 {maxLength} 个字符。");
                }
                else if (WorkflowTemplate.Validate(text) is { } templateError)
                {
                    add($"{path}.{field}", templateError);
                }
            }

            var normalized = new WorkflowStep { Key = key, Name = name, Kind = step.Kind, When = when, ContinueOnError = step.ContinueOnError };
            switch (step.Kind)
            {
                case WorkflowStepKind.Action:
                    var actionKey = step.ActionKey?.Trim();
                    var action = string.IsNullOrEmpty(actionKey) ? null : actions.Find(actionKey);
                    if (action is null || !enabledModules.Contains(action.ModuleKey))
                    {
                        add($"{path}.actionKey", string.IsNullOrEmpty(actionKey) ? "请选择要执行的动作。" : $"动作 {actionKey} 不存在或所属模块未启用。");
                    }
                    else if (!tenant.Can(action.RequiredFeature))
                    {
                        add($"{path}.actionKey", $"执行「{action.Title}」需要权限 {action.RequiredFeature}。");
                    }

                    JsonElement? input = step.Input is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } value ? value : null;
                    if (input is { } inputValue)
                    {
                        if (inputValue.ValueKind != JsonValueKind.Object)
                        {
                            add($"{path}.input", "动作输入必须是 JSON 对象。");
                        }
                        else if (WorkflowTemplate.Validate(inputValue) is { } inputError)
                        {
                            add($"{path}.input", inputError);
                        }
                    }

                    normalized = normalized with { ActionKey = actionKey, Input = input?.Clone() };
                    break;

                case WorkflowStepKind.AgentTask:
                    if (step.AgentId is not { } agentId)
                    {
                        add($"{path}.agentId", "请选择负责的智能体。");
                    }
                    else if (!agents.TryGetValue(agentId, out var status))
                    {
                        add($"{path}.agentId", "智能体不存在。");
                    }
                    else if (status == AgentStatus.Terminated)
                    {
                        add($"{path}.agentId", "不能把任务派给已终止的智能体。");
                    }

                    if (!tenant.Can(Features.Tasks.Manage))
                    {
                        add($"{path}.agentId", $"派发任务需要权限 {Features.Tasks.Manage}。");
                    }

                    CheckTemplate("title", step.Title, 300, required: true);
                    CheckTemplate("instructions", step.Instructions, 20_000, required: false);
                    if (step.WaitForCompletion && step.TimeoutMinutes is { } timeout && (timeout < 1 || timeout > MaxTimeoutMinutes))
                    {
                        add($"{path}.timeoutMinutes", $"等待时长需在 1 到 {MaxTimeoutMinutes} 分钟之间。");
                    }

                    normalized = normalized with
                    {
                        AgentId = step.AgentId,
                        Title = step.Title?.Trim(),
                        Instructions = string.IsNullOrWhiteSpace(step.Instructions) ? null : step.Instructions.Trim(),
                        WaitForCompletion = step.WaitForCompletion,
                        TimeoutMinutes = step.WaitForCompletion ? step.TimeoutMinutes : null,
                    };
                    break;

                case WorkflowStepKind.Llm:
                    CheckTemplate("prompt", step.Prompt, 20_000, required: true);
                    normalized = normalized with { Prompt = step.Prompt?.Trim() };
                    break;

                case WorkflowStepKind.Delay:
                    if (step.DelaySeconds is not { } seconds || seconds < 1 || seconds > MaxDelaySeconds)
                    {
                        add($"{path}.delaySeconds", $"等待时长需在 1 秒到 {MaxDelaySeconds / 86400} 天之间。");
                    }

                    normalized = normalized with { DelaySeconds = step.DelaySeconds };
                    break;

                default:
                    add($"{path}.kind", "未知的步骤类型。");
                    break;
            }

            result.Add(normalized);
        }

        return result;
    }
}
