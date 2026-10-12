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
        [FromQuery] int pageSize = 50)
        => SendAsync(new ListWorkItemsQuery(status, assigneeAgentId, parentId, search, openOnly, page, pageSize));

    [HttpGet("{id:guid}")]
    public Task<WorkItemDetailDto> Get(Guid id) => SendAsync(new GetWorkItemQuery(id));

    [HttpPost]
    public Task<WorkItemDto> Create(CreateWorkItemRequest request) => SendAsync(new CreateWorkItemCommand(request));

    [HttpPut("{id:guid}")]
    public Task<WorkItemDto> Update(Guid id, UpdateWorkItemRequest request) => SendAsync(new UpdateWorkItemCommand(id, request));

    [HttpPost("{id:guid}/comments")]
    public Task<CommentDto> AddComment(Guid id, AddCommentRequest request) => SendAsync(new AddCommentCommand(id, request.Body));
}
