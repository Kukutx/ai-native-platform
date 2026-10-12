using System.ComponentModel;
using System.Text.Json;
using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Actions;
using AiNative.Contracts.Workflows;
using AiNative.Domain.Workflows;

namespace AiNative.Application.Workflows.Actions;

public sealed record WorkflowBrief(
    Guid Id,
    string Name,
    string Description,
    bool IsEnabled,
    WorkflowTriggerDto Trigger,
    int StepCount,
    WorkflowRunStatus? LastRunStatus,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? NextRunAt);

public sealed class ListWorkflowsInput;

internal sealed class ListWorkflowsAction(IRequestDispatcher dispatcher) : PlatformAction<ListWorkflowsInput, IReadOnlyList<WorkflowBrief>>
{
    public override string Key => "workflows.list";

    public override string ModuleKey => WorkflowsModule.ModuleKey;

    public override string Title => "查看工作流";

    public override string Description => "列出空间里的自动化工作流：名称、触发方式、是否启用、最近一次运行结果。修改或运行前先用它找到工作流 ID。";

    public override string RequiredFeature => Features.Workflows.View;

    public override bool IsMutation => false;

    protected override async Task<IReadOnlyList<WorkflowBrief>> ExecuteAsync(ListWorkflowsInput input, ActionContext context, CancellationToken cancellationToken)
    {
        var workflows = await dispatcher.SendAsync(new ListWorkflowsQuery(), cancellationToken).ConfigureAwait(false);
        return
        [
            .. workflows.Select(w => new WorkflowBrief(
                w.Id, w.Name, w.Description, w.IsEnabled, w.Trigger, w.Steps.Count, w.LastRun?.Status, w.LastRun?.CreatedAt, w.NextRunAt)),
        ];
    }
}

public sealed class ListWorkflowEventsInput;

internal sealed class ListWorkflowEventsAction(IRequestDispatcher dispatcher) : PlatformAction<ListWorkflowEventsInput, WorkflowCatalogDto>
{
    public override string Key => "workflows.list_events";

    public override string ModuleKey => WorkflowsModule.ModuleKey;

    public override string Title => "查看可订阅的事件";

    public override string Description => "列出工作流可以订阅的平台事件，以及每个事件载荷里有哪些字段（表达式里用 trigger.xxx 引用）。设计事件触发的工作流前先调用。";

    public override string RequiredFeature => Features.Workflows.View;

    public override bool IsMutation => false;

    protected override Task<WorkflowCatalogDto> ExecuteAsync(ListWorkflowEventsInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(new GetWorkflowCatalogQuery(), cancellationToken);
}

public sealed class GetWorkflowInput
{
    [Description("工作流 ID")]
    public required Guid WorkflowId { get; init; }
}

internal sealed class GetWorkflowAction(IRequestDispatcher dispatcher) : PlatformAction<GetWorkflowInput, WorkflowDto>
{
    public override string Key => "workflows.get";

    public override string ModuleKey => WorkflowsModule.ModuleKey;

    public override string Title => "查看工作流详情";

    public override string Description => "查看工作流的完整定义（触发器与全部步骤）和最近一次运行。修改工作流前先读取，再在原定义基础上改。";

    public override string RequiredFeature => Features.Workflows.View;

    public override bool IsMutation => false;

    protected override Task<WorkflowDto> ExecuteAsync(GetWorkflowInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(new GetWorkflowQuery(input.WorkflowId), cancellationToken);
}

public class WorkflowDefinitionInput
{
    [Description("工作流名称，简短说明它做什么")]
    public required string Name { get; init; }

    [Description("说明：什么时候运行、做什么、为什么")]
    public string Description { get; init; } = string.Empty;

    [Description("是否启用。停用后不会被事件或定时触发，但仍可手动运行")]
    public bool IsEnabled { get; init; } = true;

    [Description("触发方式")]
    public required WorkflowTriggerDto Trigger { get; init; }

    [Description("按顺序执行的步骤")]
    public required List<WorkflowStep> Steps { get; init; }

    public SaveWorkflowRequest ToRequest(DateTimeOffset? expectedUpdatedAt = null) => new()
    {
        Name = Name,
        Description = Description,
        IsEnabled = IsEnabled,
        Trigger = Trigger,
        Steps = Steps,
        ExpectedUpdatedAt = expectedUpdatedAt,
    };
}

public sealed class UpdateWorkflowInput : WorkflowDefinitionInput
{
    [Description("要修改的工作流 ID")]
    public required Guid WorkflowId { get; init; }

    [Description("读取工作流时得到的 updatedAt，原样传回。期间被别人改过会返回冲突，这时重新读取、在最新定义上修改后再提交")]
    public required DateTimeOffset ExpectedUpdatedAt { get; init; }
}

internal static class WorkflowActionText
{
    public const string DefinitionGuide =
        "trigger.kind 取值：manual（手动运行）；event（平台事件触发：eventType 填事件键，可先用 workflows.list_events 查看；filter 为可选的过滤条件，例如 trigger.to == 'done'）；" +
        "schedule（定时：cron 为 5 段 cron「分 时 日 月 周」，例如 \"0 9 * * MON-FRI\"；timeZone 为 IANA 时区，例如 Asia/Shanghai）；" +
        "webhook（外部系统 POST 到工作流专属地址时触发，保存后由成员在界面上复制地址；trigger.body 是请求体，trigger.query 是查询参数，trigger.headers 是小写的请求头；filter 可选）。" +
        "steps 按顺序执行，每步需要在整个工作流里唯一的 key（小写字母开头，字母数字下划线）和 name，kind 取值：" +
        "action（执行平台动作：actionKey 为动作键，input 为输入对象）；" +
        "agent_task（派任务给智能体：agentId、title、instructions；waitForCompletion 为 true 时等它完成，输出里的 result 是智能体写在评论里的结论，可选 timeoutMinutes）；" +
        "llm（让模型处理文本：prompt，输出 text，内容是 JSON 时另有 json）；" +
        "delay（等待 delaySeconds 秒）；" +
        "http（调用外部接口：method 为 GET/POST/PUT/PATCH/DELETE，url，可选 headers 对象和 body，timeoutSeconds 默认 30；输出 status、ok、contentType、body，返回 JSON 时 body 是解析后的对象；非 2xx 视为失败；默认不能访问内网地址）；" +
        "branch（条件分支：branches 为数组，每项有 name、when 和 steps，按顺序执行第一个 when 成立的分支，最后一项可以不写 when 表示其他情况；输出 index 和 branch，没有分支成立时 index 为 -1）；" +
        "for_each（遍历：items 为结果是列表的表达式，例如 steps.find.output.items；steps 是对每一项执行的子步骤，子步骤里用 item 引用当前项、index 引用序号；输出 count 和 results，results 是每一项最后一个子步骤的输出）。" +
        "分支和遍历可以嵌套，最多 3 层，整个工作流最多 50 个步骤。" +
        "title、instructions、prompt、url、headers 的值以及 input、body 里的字符串值都可以写 {{ 表达式 }}：trigger.xxx 是触发数据，input.xxx 是手动运行参数，steps.<key>.output.xxx 是前面步骤的输出。" +
        "when 为执行条件表达式（为假时跳过本步），支持 == != > >= < <= && || ! contains 和 length() lower() default() 等函数。continueOnError 为 true 时本步失败也继续。" +
        "工作流以保存者的身份和权限运行。";
}

internal sealed class CreateWorkflowAction(IRequestDispatcher dispatcher) : PlatformAction<WorkflowDefinitionInput, WorkflowDto>
{
    public override string Key => "workflows.create";

    public override string ModuleKey => WorkflowsModule.ModuleKey;

    public override string Title => "创建工作流";

    public override string Description => "创建自动化工作流，把反复出现的固定流程交给平台自动执行。" + WorkflowActionText.DefinitionGuide;

    public override string RequiredFeature => Features.Workflows.Manage;

    // 自动化会在无人值守时反复执行，智能体搭建的工作流需要人确认后才生效。
    public override ApprovalPolicy ApprovalPolicy => ApprovalPolicy.RequiredForAgents;

    protected override Task<WorkflowDto> ExecuteAsync(WorkflowDefinitionInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(new CreateWorkflowCommand(input.ToRequest()), cancellationToken);
}

internal sealed class UpdateWorkflowAction(IRequestDispatcher dispatcher) : PlatformAction<UpdateWorkflowInput, WorkflowDto>
{
    public override string Key => "workflows.update";

    public override string ModuleKey => WorkflowsModule.ModuleKey;

    public override string Title => "修改工作流";

    public override string Description => "用完整的新定义替换工作流（先用 workflows.get 读取原定义，在其基础上修改后整体提交，并把读到的 updatedAt 作为 expectedUpdatedAt 传回）。保存后工作流改以你的身份和权限运行。" + WorkflowActionText.DefinitionGuide;

    public override string RequiredFeature => Features.Workflows.Manage;

    public override ApprovalPolicy ApprovalPolicy => ApprovalPolicy.RequiredForAgents;

    protected override Task<WorkflowDto> ExecuteAsync(UpdateWorkflowInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(new UpdateWorkflowCommand(input.WorkflowId, input.ToRequest(input.ExpectedUpdatedAt)), cancellationToken);
}

public sealed class RunWorkflowInput
{
    [Description("工作流 ID")]
    public required Guid WorkflowId { get; init; }

    [Description("运行参数（JSON 对象），步骤里用 input.xxx 引用；不需要时省略")]
    public JsonElement? Input { get; init; }
}

internal sealed class RunWorkflowAction(IRequestDispatcher dispatcher) : PlatformAction<RunWorkflowInput, WorkflowRunSummaryDto>
{
    public override string Key => "workflows.run";

    public override string ModuleKey => WorkflowsModule.ModuleKey;

    public override string Title => "运行工作流";

    public override string Description => "立即运行一次工作流（停用的也可以手动运行）。运行在后台进行，返回运行 ID，之后可用 workflows.get_run 查看进度和每一步的结果。";

    public override string RequiredFeature => Features.Workflows.Run;

    protected override Task<WorkflowRunSummaryDto> ExecuteAsync(RunWorkflowInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(new StartWorkflowRunCommand(input.WorkflowId, input.Input), cancellationToken);
}

public sealed class GetWorkflowRunInput
{
    [Description("运行 ID")]
    public required Guid RunId { get; init; }
}

internal sealed class GetWorkflowRunAction(IRequestDispatcher dispatcher) : PlatformAction<GetWorkflowRunInput, WorkflowRunDto>
{
    public override string Key => "workflows.get_run";

    public override string ModuleKey => WorkflowsModule.ModuleKey;

    public override string Title => "查看工作流运行";

    public override string Description => "查看一次工作流运行的状态、触发数据，以及每一步的输入、输出和错误。";

    public override string RequiredFeature => Features.Workflows.View;

    public override bool IsMutation => false;

    protected override Task<WorkflowRunDto> ExecuteAsync(GetWorkflowRunInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(new GetWorkflowRunQuery(input.RunId), cancellationToken);
}
