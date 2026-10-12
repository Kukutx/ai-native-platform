using AiNative.Application.Work;
using AiNative.Contracts.Common;
using AiNative.Contracts.Work;
using AiNative.Domain.Work;
using Microsoft.AspNetCore.Mvc;

namespace AiNative.Api.Controllers;

[Route("api/work-items")]
public sealed class WorkItemsController : ApiControllerBase
{
    [HttpGet]
    public Task<PagedResult<WorkItemDto>> List(
        [FromQuery] WorkItemStatus? status,
        [FromQuery] Guid? assigneeAgentId,
        [FromQuery] Guid? parentId,
        [FromQuery] string? search,
        [FromQuery] bool openOnly = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] Guid? projectId = null,
        [FromQuery] Guid? milestoneId = null)
        => SendAsync(new ListWorkItemsQuery(status, assigneeAgentId, parentId, search, openOnly, page, pageSize, projectId, milestoneId));

    [HttpGet("{id:guid}")]
    public Task<WorkItemDetailDto> Get(Guid id) => SendAsync(new GetWorkItemQuery(id));

    [HttpPost]
    public Task<WorkItemDto> Create(CreateWorkItemRequest request) => SendAsync(new CreateWorkItemCommand(request));

    [HttpPut("{id:guid}")]
    public Task<WorkItemDto> Update(Guid id, UpdateWorkItemRequest request) => SendAsync(new UpdateWorkItemCommand(id, request));

    /// <summary>
    /// 调整开始和截止日期（甘特图拖动用）。shiftDependents 为 true 时顺延因此冲突的后续任务，沿依赖链传递，只往后挪。
    /// </summary>
    [HttpPut("{id:guid}/schedule")]
    public Task<ScheduleWorkItemResultDto> Schedule(Guid id, ScheduleWorkItemRequest request) => SendAsync(new ScheduleWorkItemCommand(id, request));

    /// <summary>
    /// 添加前置任务：这个任务要等它关闭（完成或取消）后才能开始。形成循环返回 409 work_item_dependency.cycle。
    /// </summary>
    [HttpPost("{id:guid}/dependencies")]
    public Task<WorkItemLinkDto> AddDependency(Guid id, AddWorkItemDependencyRequest request) => SendAsync(new AddWorkItemDependencyCommand(id, request.DependsOnId));

    /// <summary>
    /// 去掉前置任务。任务因此不再等任何前置任务时，负责的智能体会被唤醒。
    /// </summary>
    [HttpDelete("{id:guid}/dependencies/{dependsOnId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveDependency(Guid id, Guid dependsOnId)
    {
        await SendAsync(new RemoveWorkItemDependencyCommand(id, dependsOnId)).ConfigureAwait(false);
        return NoContent();
    }

    [HttpPost("{id:guid}/comments")]
    public Task<CommentDto> AddComment(Guid id, AddCommentRequest request) => SendAsync(new AddCommentCommand(id, request.Body));
}
