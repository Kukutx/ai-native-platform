using AiNative.Application.Abstractions;
using AiNative.Application.Knowledge;
using AiNative.Contracts.Common;
using AiNative.Contracts.Knowledge;
using AiNative.Domain.Knowledge;
using Microsoft.AspNetCore.Mvc;

namespace AiNative.Api.Controllers;

[Route("api/knowledge")]
public sealed class KnowledgeController : ApiControllerBase
{
    [HttpGet("documents")]
    public Task<IReadOnlyList<KnowledgeDocumentDto>> ListDocuments([FromQuery] string? search)
        => SendAsync(new ListKnowledgeDocumentsQuery(search));

    [HttpGet("documents/{id:guid}")]
    public Task<KnowledgeDocumentDetailDto> GetDocument(Guid id) => SendAsync(new GetKnowledgeDocumentQuery(id));

    [HttpPost("documents")]
    public Task<KnowledgeDocumentDto> CreateDocument(SaveKnowledgeDocumentRequest request)
        => SendAsync(new CreateKnowledgeDocumentCommand(request));

    [HttpPut("documents/{id:guid}")]
    public Task<KnowledgeDocumentDto> UpdateDocument(Guid id, SaveKnowledgeDocumentRequest request)
        => SendAsync(new UpdateKnowledgeDocumentCommand(id, request));

    [HttpDelete("documents/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteDocument(Guid id)
    {
        await SendAsync(new DeleteKnowledgeDocumentCommand(id)).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// 混合检索（语义 + 关键词）。有长期记忆权限时同时检索可见的记忆。
    /// </summary>
    [HttpGet("search")]
    public Task<IReadOnlyList<KnowledgeHitDto>> Search(
        [FromQuery] string q,
        [FromQuery] bool documents = true,
        [FromQuery] bool memories = true,
        [FromQuery] int limit = 10,
        [FromQuery] Guid? agentId = null)
        => SendAsync(new SearchKnowledgeQuery(q, documents, memories, limit, agentId));

    [HttpGet("status")]
    public Task<KnowledgeStatusDto> Status() => SendAsync(new GetKnowledgeStatusQuery());
}

[Route("api/memories")]
public sealed class MemoriesController : ApiControllerBase
{
    [HttpGet]
    public Task<PagedResult<MemoryDto>> List(
        [FromQuery] MemoryScope? scope,
        [FromQuery] Guid? ownerId,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
        => SendAsync(new ListMemoriesQuery(scope, ownerId, search, page, pageSize));

    [HttpPost]
    public Task<MemoryDto> Create(CreateMemoryRequest request)
        => SendAsync(new RetainMemoryCommand(request.Content, request.Shared, request.Tags));

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await SendAsync(new DeleteMemoryCommand(id)).ConfigureAwait(false);
        return NoContent();
    }
}
