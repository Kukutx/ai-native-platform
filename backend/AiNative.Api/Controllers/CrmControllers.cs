using AiNative.Application.Crm;
using AiNative.Application.Files;
using AiNative.Contracts.Common;
using AiNative.Contracts.Crm;
using AiNative.Domain.Crm;
using Microsoft.AspNetCore.Mvc;

namespace AiNative.Api.Controllers;

[Route("api/crm/companies")]
public sealed class CrmCompaniesController : ApiControllerBase
{
    /// <summary>
    /// 客户列表，最近有往来的在前；search 匹配名称、行业、电话。
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

[Route("api/crm/fields")]
public sealed class CrmFieldsController : ApiControllerBase
{
    /// <summary>
    /// 自定义字段，按记录类型和顺序排列；recordType 为空时返回全部。
    /// </summary>
    [HttpGet]
    public Task<IReadOnlyList<CrmFieldDto>> List([FromQuery] CrmRecordType? recordType) => SendAsync(new ListCrmFieldsQuery(recordType));

    [HttpPost]
    public Task<CrmFieldDto> Create(SaveCrmFieldRequest request) => SendAsync(new CreateCrmFieldCommand(request));

    /// <summary>
    /// 修改名称、说明、选项和是否必填。标识、类型和记录类型不能改；删掉的选项会从记录里清除。
    /// </summary>
    [HttpPut("{id:guid}")]
    public Task<CrmFieldDto> Update(Guid id, SaveCrmFieldRequest request) => SendAsync(new UpdateCrmFieldCommand(id, request));

    /// <summary>
    /// 删除字段，同时清除所有记录里这个字段的值。
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await SendAsync(new DeleteCrmFieldCommand(id)).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// 调整一类记录的字段顺序：提交这类记录的全部字段 ID。
    /// </summary>
    [HttpPut("order")]
    public Task<IReadOnlyList<CrmFieldDto>> Reorder(ReorderCrmFieldsRequest request) => SendAsync(new ReorderCrmFieldsCommand(request));
}

[Route("api/crm/import")]
public sealed class CrmImportController : ApiControllerBase
{
    private const long MultipartOverheadBytes = 1024 * 1024;

    /// <summary>
    /// 读取上传的表格（.csv、.xlsx），返回列、示例值和建议的对应字段。不保存任何内容。
    /// </summary>
    [HttpPost("preview")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(CrmImportLimits.MaxFileBytes + MultipartOverheadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = CrmImportLimits.MaxFileBytes + MultipartOverheadBytes)]
    public async Task<CrmImportPreviewDto> Preview(IFormFile? file, [FromForm] CrmRecordType recordType)
    {
        await using var content = OpenFile(file);
        return await SendAsync(new PreviewCrmImportQuery(recordType, file!.FileName, file.Length, content)).ConfigureAwait(false);
    }

    /// <summary>
    /// 导入客户或联系人。mapping 按列的顺序给出每列对应的字段（见预览返回的 targets），不导入的列传空字符串。
    /// 有问题的行跳过并在结果里说明原因；dryRun 为 true 时只检查不保存。
    /// </summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(CrmImportLimits.MaxFileBytes + MultipartOverheadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = CrmImportLimits.MaxFileBytes + MultipartOverheadBytes)]
    public async Task<CrmImportResultDto> Import(
        IFormFile? file,
        [FromForm] CrmRecordType recordType,
        [FromForm] List<string?>? mapping,
        [FromForm] CrmImportDuplicateMode duplicates = CrmImportDuplicateMode.Skip,
        [FromForm] bool createMissingCompanies = false,
        [FromForm] bool dryRun = false)
    {
        await using var content = OpenFile(file);
        var options = new CrmImportOptions
        {
            RecordType = recordType,
            Mapping = mapping ?? [],
            Duplicates = duplicates,
            CreateMissingCompanies = createMissingCompanies,
            DryRun = dryRun,
        };
        return await SendAsync(new ImportCrmRecordsCommand(options, file!.FileName, file.Length, content)).ConfigureAwait(false);
    }

    private static Stream OpenFile(IFormFile? file)
        => file?.OpenReadStream() ?? throw new TabularFileException("file.missing", "请选择要导入的文件。");
}
