using AiNative.Application.Agents;
using AiNative.Contracts.Agents;
using AiNative.Contracts.Common;
using Microsoft.AspNetCore.Mvc;

namespace AiNative.Api.Controllers;

[Route("api/runs")]
public sealed class RunsController : ApiControllerBase
{
    [HttpGet]
    public Task<PagedResult<RunDto>> List([FromQuery] Guid? agentId, [FromQuery] int page = 1, [FromQuery] int pageSize = 30)
        => SendAsync(new ListRunsQuery(agentId, page, pageSize));

    /// <summary>
    /// 运行详情与完整事件流（工具调用、审批请求、输出）。
    /// </summary>
    [HttpGet("{id:guid}")]
    public Task<RunDetailDto> Get(Guid id) => SendAsync(new GetRunQuery(id));
}
