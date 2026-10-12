using AiNative.Application.Workflows;
using AiNative.Contracts.Common;
using AiNative.Contracts.Workflows;
using AiNative.Domain.Workflows;
using Microsoft.AspNetCore.Mvc;

namespace AiNative.Api.Controllers;

[Route("api/workflows")]
public sealed class WorkflowsController : ApiControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<WorkflowDto>> List() => SendAsync(new ListWorkflowsQuery());

    /// <summary>
    /// 工作流可以订阅的事件（只列出当前身份有权订阅的）。
    /// </summary>
    [HttpGet("catalog")]
    public Task<WorkflowCatalogDto> Catalog() => SendAsync(new GetWorkflowCatalogQuery());

    [HttpGet("{id:guid}")]
    public Task<WorkflowDto> Get(Guid id) => SendAsync(new GetWorkflowQuery(id));

    [HttpPost]
    public Task<WorkflowDto> Create(SaveWorkflowRequest request) => SendAsync(new CreateWorkflowCommand(request));

    [HttpPut("{id:guid}")]
    public Task<WorkflowDto> Update(Guid id, SaveWorkflowRequest request) => SendAsync(new UpdateWorkflowCommand(id, request));

    [HttpPut("{id:guid}/enabled")]
    public Task<WorkflowDto> SetEnabled(Guid id, SetWorkflowEnabledRequest request) => SendAsync(new SetWorkflowEnabledCommand(id, request.Enabled));

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await SendAsync(new DeleteWorkflowCommand(id)).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// 换一个新的 Webhook 地址，旧地址立即失效。
    /// </summary>
    [HttpPost("{id:guid}/webhook-token")]
    public Task<WorkflowDto> RegenerateWebhook(Guid id) => SendAsync(new RegenerateWorkflowWebhookCommand(id));

    /// <summary>
    /// 手动运行一次。运行在后台进行，返回的运行记录通过实时通道持续更新。
    /// </summary>
    [HttpPost("{id:guid}/runs")]
    public Task<WorkflowRunSummaryDto> Start(Guid id, StartWorkflowRunRequest request) => SendAsync(new StartWorkflowRunCommand(id, request.Input));

    [HttpGet("runs")]
    public Task<PagedResult<WorkflowRunSummaryDto>> Runs(
        [FromQuery] Guid? workflowId,
        [FromQuery] WorkflowRunStatus? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 30)
        => SendAsync(new ListWorkflowRunsQuery(workflowId, status, page, pageSize));

    [HttpGet("runs/{runId:guid}")]
    public Task<WorkflowRunDto> Run(Guid runId) => SendAsync(new GetWorkflowRunQuery(runId));

    [HttpPost("runs/{runId:guid}/cancel")]
    public Task<WorkflowRunSummaryDto> Cancel(Guid runId) => SendAsync(new CancelWorkflowRunCommand(runId));
}
