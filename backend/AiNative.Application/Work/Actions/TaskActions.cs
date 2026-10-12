using System.ComponentModel;
using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Actions;
using AiNative.Application.Agents.Actions;
using AiNative.Application.Common;
using AiNative.Contracts.Work;
using AiNative.Domain.Work;
using Microsoft.EntityFrameworkCore;

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

    [Description("只看某个项目里的任务（项目 ID）；按项目查询时默认看全部负责人")]
    public Guid? ProjectId { get; init; }

    [Description("只看某个里程碑里的任务（里程碑 ID）")]
    public Guid? MilestoneId { get; init; }

    [Description("最多返回条数，默认 20")]
    public int Limit { get; init; } = 20;
}

internal sealed class ListTasksAction(IRequestDispatcher dispatcher) : PlatformAction<ListTasksInput, IReadOnlyList<WorkItemDto>>
{
    public override string Key => "tasks.list";

    public override string ModuleKey => AgentsModuleKeys.Agents;

    public override string Title => "查询任务";

    public override string Description => "查询任务列表。默认返回调用者自己负责、尚未关闭的任务；给出 projectId 或 milestoneId 时返回该项目或里程碑里全部负责人的任务。";

    public override string RequiredFeature => Features.Tasks.View;

    public override bool IsMutation => false;

    protected override async Task<IReadOnlyList<WorkItemDto>> ExecuteAsync(ListTasksInput input, ActionContext context, CancellationToken cancellationToken)
    {
        var scoped = input.ProjectId is not null || input.MilestoneId is not null;
        var assignee = input.AllAssignees ? null : input.AssigneeAgentId ?? (context.Actor.IsAgent && !scoped ? context.Actor.Id : null);
        var result = await dispatcher.SendAsync(
            new ListWorkItemsQuery(input.Status, assignee, null, input.Search, input.OpenOnly, 1, Math.Clamp(input.Limit, 1, 100), input.ProjectId, input.MilestoneId),
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

    public override string Description => "查看任务详情，包括描述、子任务、全部评论，以及前置任务（dependencies，都关闭后才能开始）和后续任务（dependents）。";

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

    [Description("放进哪个项目（项目 ID，可用 projects.list 查到）；拆分子任务时省略则沿用上级任务的项目和里程碑")]
    public Guid? ProjectId { get; init; }

    [Description("放进哪个里程碑（里程碑 ID，可用 projects.get 查到）；只填它时项目自动取里程碑所在的项目")]
    public Guid? MilestoneId { get; init; }

    [Description("计划开始日期，格式 yyyy-MM-dd；不能晚于截止日期")]
    public DateOnly? StartDate { get; init; }

    [Description("截止日期，格式 yyyy-MM-dd")]
    public DateOnly? DueDate { get; init; }

    [Description("前置任务 ID 列表：它们都关闭（完成或取消）后这个任务才能开始。指派给智能体时，对方要等前置任务都关闭后才会被唤醒")]
    public IReadOnlyList<Guid> DependsOn { get; init; } = [];
}

internal sealed class CreateTaskAction(IRequestDispatcher dispatcher) : PlatformAction<CreateTaskInput, WorkItemDto>
{
    public override string Key => "tasks.create";

    public override string ModuleKey => AgentsModuleKeys.Agents;

    public override string Title => "创建任务";

    public override string Description => "创建任务或子任务，可直接指派给某个智能体（被指派者会自动被唤醒），可放进项目和里程碑、设定开始和截止日期、指定前置任务。用于拆解工作、委派给下属；规划有先后顺序的工作时用 dependsOn 串起来。";

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
                ProjectId = input.ProjectId,
                MilestoneId = input.MilestoneId,
                StartDate = input.StartDate,
                DueDate = input.DueDate,
                DependsOn = input.DependsOn,
            }),
            cancellationToken);
}

public sealed class ScheduleTaskInput
{
    [Description("任务 ID")]
    public required Guid TaskId { get; init; }

    [Description("移到哪个项目（项目 ID）；不改就省略")]
    public Guid? ProjectId { get; init; }

    [Description("移到哪个里程碑（里程碑 ID）；只填它时项目自动取里程碑所在的项目")]
    public Guid? MilestoneId { get; init; }

    [Description("为 true 时把任务移出项目（同时移出里程碑）")]
    public bool RemoveFromProject { get; init; }

    [Description("新的开始日期，格式 yyyy-MM-dd；不改就省略")]
    public DateOnly? StartDate { get; init; }

    [Description("为 true 时清除开始日期")]
    public bool ClearStartDate { get; init; }

    [Description("新的截止日期，格式 yyyy-MM-dd；不改就省略")]
    public DateOnly? DueDate { get; init; }

    [Description("为 true 时清除截止日期")]
    public bool ClearDueDate { get; init; }

    [Description("为 true 时顺延后续任务：开始日期早于本任务截止日期的未关闭后续任务连同它们的后续任务一起往后挪，只往后不往前。任务延期时用它保持排期不冲突")]
    public bool ShiftDependents { get; init; }
}

internal sealed class ScheduleTaskAction(IRequestDispatcher dispatcher, IAppDbContext db) : PlatformAction<ScheduleTaskInput, ScheduleWorkItemResultDto>
{
    public override string Key => "tasks.schedule";

    public override string ModuleKey => AgentsModuleKeys.Agents;

    public override string Title => "安排任务";

    public override string Description => "调整已有任务所属的项目、里程碑和开始、截止日期，只需填写要改的部分；shiftDependents 为 true 时顺延因此冲突的后续任务。返回调整后的任务（item）和被顺延的任务（shifted）。规划项目时用它把任务归入里程碑、排开时间。";

    public override string RequiredFeature => Features.Tasks.Manage;

    protected override async Task<ScheduleWorkItemResultDto> ExecuteAsync(ScheduleTaskInput input, ActionContext context, CancellationToken cancellationToken)
    {
        // 直接查库读取当前内容，只要求本动作声明的权限。
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == input.TaskId, cancellationToken).ConfigureAwait(false)
                   ?? throw new NotFoundException("work_item", input.TaskId);
        var (projectId, milestoneId) = input.RemoveFromProject
            ? (null, null)
            : input.ProjectId is not null || input.MilestoneId is not null
                ? (input.ProjectId, input.MilestoneId)
                : (item.ProjectId, item.MilestoneId);
        var startDate = input.ClearStartDate ? null : input.StartDate ?? item.StartDate;
        var dueDate = input.ClearDueDate ? null : input.DueDate ?? item.DueDate;

        // 基于读到的版本修改，期间被别人改过就返回冲突，不会覆盖别人的修改。
        DateTimeOffset? expected = item.UpdatedAt;
        if (projectId != item.ProjectId || milestoneId != item.MilestoneId)
        {
            var moved = await dispatcher.SendAsync(
                new UpdateWorkItemCommand(item.Id, new UpdateWorkItemRequest
                {
                    Title = item.Title,
                    Description = item.Description,
                    Status = item.Status,
                    Priority = item.Priority,
                    ProjectId = projectId,
                    MilestoneId = milestoneId,
                    StartDate = startDate,
                    DueDate = dueDate,
                    AssigneeAgentId = item.AssigneeAgentId,
                    AssigneeUserId = item.AssigneeUserId,
                    ExpectedUpdatedAt = expected,
                }),
                cancellationToken).ConfigureAwait(false);
            if (!input.ShiftDependents)
            {
                return new ScheduleWorkItemResultDto(moved, []);
            }

            // 同一事务里刚检查并改过，接着顺延不用再比对版本。
            expected = null;
        }

        return await dispatcher.SendAsync(
            new ScheduleWorkItemCommand(item.Id, new ScheduleWorkItemRequest
            {
                StartDate = startDate,
                DueDate = dueDate,
                ShiftDependents = input.ShiftDependents,
                ExpectedUpdatedAt = expected,
            }),
            cancellationToken).ConfigureAwait(false);
    }
}

public sealed class TaskDependencyInput
{
    [Description("要等待的任务（后续任务）ID")]
    public required Guid TaskId { get; init; }

    [Description("前置任务 ID：它关闭（完成或取消）后 taskId 才能开始")]
    public required Guid DependsOnTaskId { get; init; }
}

internal sealed class AddTaskDependencyAction(IRequestDispatcher dispatcher) : PlatformAction<TaskDependencyInput, WorkItemLinkDto>
{
    public override string Key => "tasks.add_dependency";

    public override string ModuleKey => AgentsModuleKeys.Agents;

    public override string Title => "添加前置任务";

    public override string Description => "让一个任务等另一个任务关闭（完成或取消）后才能开始。前置任务未关闭时负责的智能体领取不到后续任务，前置任务都关闭后它会被唤醒。不能形成循环。返回前置任务的摘要。";

    public override string RequiredFeature => Features.Tasks.Manage;

    protected override Task<WorkItemLinkDto> ExecuteAsync(TaskDependencyInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(new AddWorkItemDependencyCommand(input.TaskId, input.DependsOnTaskId), cancellationToken);
}

internal sealed class RemoveTaskDependencyAction(IRequestDispatcher dispatcher) : PlatformAction<TaskDependencyInput, WorkItemLinkDto>
{
    public override string Key => "tasks.remove_dependency";

    public override string ModuleKey => AgentsModuleKeys.Agents;

    public override string Title => "去掉前置任务";

    public override string Description => "去掉一条前置任务关系。任务因此不再等待任何前置任务时，负责的智能体会被唤醒。返回被去掉的前置任务的摘要。";

    public override string RequiredFeature => Features.Tasks.Manage;

    protected override Task<WorkItemLinkDto> ExecuteAsync(TaskDependencyInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(new RemoveWorkItemDependencyCommand(input.TaskId, input.DependsOnTaskId), cancellationToken);
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
