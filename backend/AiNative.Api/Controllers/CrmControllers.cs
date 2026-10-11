using AiNative.Application.Crm;
using AiNative.Contracts.Common;
using AiNative.Contracts.Crm;
using AiNative.Domain.Crm;
using Microsoft.AspNetCore.Mvc;

namespace AiNative.Api.Controllers;

[Route("api/crm/companies")]
public sealed class CrmCompaniesController : ApiControllerBase
{
    /// <summary>
    /// 客户列表，最近有往来的在前；search 匹配名称、行业、电话、官网。
    /// </summary>
    [HttpGet]
    public Task<PagedResult<CrmCompanyDto>> List(
        [FromQuery] string? search,
        [FromQuery] Guid? ownerUserId,
        [FromQuery] string? tag,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
        => SendAsync(new ListCrmCompaniesQuery(search, ownerUserId, tag, page, pageSize));

    [HttpGet("{id:guid}")]
    public Task<CrmCompanyDetailDto> Get(Guid id) => SendAsync(new GetCrmCompanyQuery(id));

    [HttpPost]
    public Task<CrmCompanyDto> Create(SaveCrmCompanyRequest request) => SendAsync(new CreateCrmCompanyCommand(request));

    [HttpPut("{id:guid}")]
    public Task<CrmCompanyDto> Update(Guid id, SaveCrmCompanyRequest request) => SendAsync(new UpdateCrmCompanyCommand(id, request));

    /// <summary>
    /// 删除客户。名下联系人和商机保留，只解除关联；只挂在该客户上的跟进记录一并删除。
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await SendAsync(new DeleteCrmCompanyCommand(id)).ConfigureAwait(false);
        return NoContent();
    }
}

[Route("api/crm/contacts")]
public sealed class CrmContactsController : ApiControllerBase
{
    [HttpGet]
    public Task<PagedResult<CrmContactDto>> List(
        [FromQuery] string? search,
        [FromQuery] Guid? companyId,
        [FromQuery] Guid? ownerUserId,
        [FromQuery] string? tag,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
        => SendAsync(new ListCrmContactsQuery(search, companyId, ownerUserId, tag, page, pageSize));

    [HttpGet("{id:guid}")]
    public Task<CrmContactDetailDto> Get(Guid id) => SendAsync(new GetCrmContactQuery(id));

    [HttpPost]
    public Task<CrmContactDto> Create(SaveCrmContactRequest request) => SendAsync(new CreateCrmContactCommand(request));

    [HttpPut("{id:guid}")]
    public Task<CrmContactDto> Update(Guid id, SaveCrmContactRequest request) => SendAsync(new UpdateCrmContactCommand(id, request));

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await SendAsync(new DeleteCrmContactCommand(id)).ConfigureAwait(false);
        return NoContent();
    }
}

[Route("api/crm/deals")]
public sealed class CrmDealsController : ApiControllerBase
{
    [HttpGet]
    public Task<PagedResult<CrmDealDto>> List(
        [FromQuery] string? search,
        [FromQuery] Guid? stageId,
        [FromQuery] CrmDealStatus? status,
        [FromQuery] Guid? companyId,
        [FromQuery] Guid? contactId,
        [FromQuery] Guid? ownerUserId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
        => SendAsync(new ListCrmDealsQuery(search, stageId, status, companyId, contactId, ownerUserId, page, pageSize));

    /// <summary>
    /// 销售看板：按阶段分列的商机与金额合计（赢单、输单列只含最近结束的）。
    /// </summary>
    [HttpGet("pipeline")]
    public Task<CrmPipelineDto> Pipeline([FromQuery] Guid? ownerUserId) => SendAsync(new GetCrmPipelineQuery(ownerUserId));

    [HttpGet("{id:guid}")]
    public Task<CrmDealDetailDto> Get(Guid id) => SendAsync(new GetCrmDealQuery(id));

    [HttpPost]
    public Task<CrmDealDto> Create(SaveCrmDealRequest request) => SendAsync(new CreateCrmDealCommand(request));

    [HttpPut("{id:guid}")]
    public Task<CrmDealDto> Update(Guid id, SaveCrmDealRequest request) => SendAsync(new UpdateCrmDealCommand(id, request));

    /// <summary>
    /// 推进到另一个阶段（看板拖动）。阶段变化自动记入跟进记录；商机已被别人移走时返回 409。
    /// </summary>
    [HttpPost("{id:guid}/stage")]
    public Task<CrmDealDto> Move(Guid id, MoveCrmDealRequest request) => SendAsync(new MoveCrmDealCommand(id, request.StageId, request.Note));

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await SendAsync(new DeleteCrmDealCommand(id)).ConfigureAwait(false);
        return NoContent();
    }
}

[Route("api/crm/activities")]
public sealed class CrmActivitiesController : ApiControllerBase
{
    /// <summary>
    /// 跟进记录，最近发生的在前。按客户查询时包含名下联系人和商机的记录。
    /// </summary>
    [HttpGet]
    public Task<PagedResult<CrmActivityDto>> List(
        [FromQuery] Guid? companyId,
        [FromQuery] Guid? contactId,
        [FromQuery] Guid? dealId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
        => SendAsync(new ListCrmActivitiesQuery(companyId, contactId, dealId, page, pageSize));

    [HttpPost]
    public Task<CrmActivityDto> Log(LogCrmActivityRequest request) => SendAsync(new LogCrmActivityCommand(request));

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await SendAsync(new DeleteCrmActivityCommand(id)).ConfigureAwait(false);
        return NoContent();
    }
}

[Route("api/crm/stages")]
public sealed class CrmStagesController : ApiControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<CrmStageDto>> List() => SendAsync(new ListCrmStagesQuery());

    /// <summary>
    /// 整体保存销售阶段（顺序即列表顺序）。带 id 的是改名或调整，不带 id 的是新增，缺席的会被删除（仍有商机时返回 409）。
    /// </summary>
    [HttpPut]
    public Task<IReadOnlyList<CrmStageDto>> Save(SaveCrmStagesRequest request) => SendAsync(new SaveCrmStagesCommand(request));
}
