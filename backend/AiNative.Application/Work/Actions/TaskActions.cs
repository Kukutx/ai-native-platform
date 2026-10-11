using System.ComponentModel;
using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Actions;
using AiNative.Application.Agents.Actions;
using AiNative.Contracts.Work;
using AiNative.Domain.Work;

namespace AiNative.Application.Work.Actions;

public sealed class ListTasksInput
{
    [Description("只看指派给某个智能体的任务；智能体调用时留空表示只看自己的")]
    public Guid? AssigneeAgentId { get; init; }

    [Description("为 true 时查看全部负责人的任务")]
    public bool AllAssignees { get; init; }

    [Description("按状态过滤")]
    public WorkItemStatus? Status { get; init; }

    [Description("只看未关闭的任务，默认 true")]
    public bool OpenOnly { get; init; } = true;

    [Description("标题或描述关键字")]
    public string? Search { get; init; }

    [Description("最多返回条数，默认 20")]
    public int Limit { get; init; } = 20;
}

internal sealed class ListTasksAction(IRequestDispatcher dispatcher) : PlatformAction<ListTasksInput, IReadOnlyList<WorkItemDto>>
{
    public override string Key => "tasks.list";

    public override string ModuleKey => AgentsModuleKeys.Agents;

    public override string Title => "查询任务";

    public override string Description => "查询任务列表。默认返回调用者自己负责、尚未关闭的任务。";

    public override string RequiredFeature => Features.Tasks.View;

    public override bool IsMutation => false;

    protected override async Task<IReadOnlyList<WorkItemDto>> ExecuteAsync(ListTasksInput input, ActionContext context, CancellationToken cancellationToken)
    {
        var assignee = input.AllAssignees ? null : input.AssigneeAgentId ?? (context.Actor.IsAgent ? context.Actor.Id : null);
        var result = await dispatcher.SendAsync(
            new ListWorkItemsQuery(input.Status, assignee, null, input.Search, input.OpenOnly, 1, Math.Clamp(input.Limit, 1, 100)),
            cancellationToken).ConfigureAwait(false);
        return result.Items;
    }
}

public sealed class GetTaskInput
{
    [Description("任务 ID")]
    public required Guid TaskId { get; init; }
}

internal sealed class GetTaskAction(IRequestDispatcher dispatcher) : PlatformAction<GetTaskInput, WorkItemDetailDto>
{
    public override string Key => "tasks.get";

    public override string ModuleKey => AgentsModuleKeys.Agents;

    public override string Title => "查看任务详情";

    public override string Description => "查看任务详情，包括描述、子任务和全部评论。";

    public override string RequiredFeature => Features.Tasks.View;

    public override bool IsMutation => false;

    protected override Task<WorkItemDetailDto> ExecuteAsync(GetTaskInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(new GetWorkItemQuery(input.TaskId), cancellationToken);
}

public sealed class CreateTaskInput
{
    [Description("任务标题，一句话说明要做什么")]
    public required string Title { get; init; }

    [Description("背景、要求和验收标准")]
    public string Description { get; init; } = string.Empty;

    [Description("优先级")]
    public WorkItemPriority Priority { get; init; } = WorkItemPriority.Medium;

    [Description("上级任务 ID；拆分子任务时填写")]
    public Guid? ParentId { get; init; }

    [Description("指派给哪个智能体；指派后对方会被自动唤醒")]
    public Guid? AssigneeAgentId { get; init; }
}

internal sealed class CreateTaskAction(IRequestDispatcher dispatcher) : PlatformAction<CreateTaskInput, WorkItemDto>
{
    public override string Key => "tasks.create";

    public override string ModuleKey => AgentsModuleKeys.Agents;

    public override string Title => "创建任务";

    public override string Description => "创建任务或子任务，可直接指派给某个智能体（被指派者会自动被唤醒）。用于拆解工作、委派给下属。";

    public override string RequiredFeature => Features.Tasks.Manage;

    protected override Task<WorkItemDto> ExecuteAsync(CreateTaskInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(
            new CreateWorkItemCommand(new CreateWorkItemRequest
            {
                Title = input.Title,
                Description = input.Description,
                Priority = input.Priority,
                ParentId = input.ParentId,
                AssigneeAgentId = input.AssigneeAgentId,
            }),
            cancellationToken);
}

public sealed class UpdateTaskStatusInput
{
    [Description("任务 ID")]
    public required Guid TaskId { get; init; }

    [Description("新状态")]
    public required WorkItemStatus Status { get; init; }

    [Description("附带的说明，会作为评论保存（建议写明结论或阻塞原因）")]
    public string? Comment { get; init; }
}

internal sealed class UpdateTaskStatusAction(IRequestDispatcher dispatcher) : PlatformAction<UpdateTaskStatusInput, WorkItemDto>
{
    public override string Key => "tasks.update_status";

    public override string ModuleKey => AgentsModuleKeys.Agents;

    public override string Title => "更新任务状态";

    public override string Description => "更新任务状态。完成写 done，需要人确认写 in_review，卡住写 blocked 并在 comment 里说明原因。";

    public override string RequiredFeature => Features.Tasks.Manage;

    protected override Task<WorkItemDto> ExecuteAsync(UpdateTaskStatusInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(new ChangeWorkItemStatusCommand(input.TaskId, input.Status, input.Comment, context.RunId), cancellationToken);
}

public sealed class AssignTaskInput
{
    [Description("任务 ID")]
    public required Guid TaskId { get; init; }

    [Description("新的负责智能体 ID；为空表示取消指派")]
    public Guid? AssigneeAgentId { get; init; }
}

internal sealed class AssignTaskAction(IRequestDispatcher dispatcher) : PlatformAction<AssignTaskInput, WorkItemDto>
{
    public override string Key => "tasks.assign";

    public override string ModuleKey => AgentsModuleKeys.Agents;

    public override string Title => "指派任务";

    public override string Description => "把已有任务转交给另一个智能体，对方会被自动唤醒。";

    public override string RequiredFeature => Features.Tasks.Manage;

    protected override Task<WorkItemDto> ExecuteAsync(AssignTaskInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(new AssignWorkItemCommand(input.TaskId, input.AssigneeAgentId, null), cancellationToken);
}

public sealed class CommentTaskInput
{
    [Description("任务 ID")]
    public required Guid TaskId { get; init; }

    [Description("评论内容：进展、结论、问题或交付物")]
    public required string Body { get; init; }
}

internal sealed class CommentTaskAction(IRequestDispatcher dispatcher) : PlatformAction<CommentTaskInput, CommentDto>
{
    public override string Key => "tasks.comment";

    public override string ModuleKey => AgentsModuleKeys.Agents;

    public override string Title => "评论任务";

    public override string Description => "在任务下留言。汇报进展、交付结果、向负责人提问都用它；任务负责人会被唤醒。";

    public override string RequiredFeature => Features.Tasks.Comment;

    protected override Task<CommentDto> ExecuteAsync(CommentTaskInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(new AddCommentCommand(input.TaskId, input.Body, context.RunId), cancellationToken);
}
