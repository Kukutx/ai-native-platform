using AiNative.Application.Agents;
using AiNative.Contracts.Agents;
using Microsoft.AspNetCore.Mvc;

namespace AiNative.Api.Controllers;

[Route("api/agents")]
public sealed class AgentsController : ApiControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<AgentDto>> List() => SendAsync(new ListAgentsQuery());

    [HttpGet("org-chart")]
    public Task<IReadOnlyList<OrgChartNodeDto>> OrgChart() => SendAsync(new GetOrgChartQuery());

    [HttpGet("{id:guid}")]
    public Task<AgentDto> Get(Guid id) => SendAsync(new GetAgentQuery(id));

    [HttpPost]
    public Task<AgentDto> Create(SaveAgentRequest request) => SendAsync(new CreateAgentCommand(request));

    [HttpPut("{id:guid}")]
    public Task<AgentDto> Update(Guid id, SaveAgentRequest request) => SendAsync(new UpdateAgentCommand(id, request));

    [HttpPost("{id:guid}/pause")]
    public Task<AgentDto> Pause(Guid id) => SendAsync(new SetAgentPausedCommand(id, true));

    [HttpPost("{id:guid}/resume")]
    public Task<AgentDto> Resume(Guid id) => SendAsync(new SetAgentPausedCommand(id, false));

    [HttpPost("{id:guid}/terminate")]
    public Task<AgentDto> Terminate(Guid id) => SendAsync(new TerminateAgentCommand(id));

    /// <summary>
    /// 手动唤醒智能体执行一轮心跳。
    /// </summary>
    [HttpPost("{id:guid}/wake")]
    public Task<WakeupDto> Wake(Guid id, WakeAgentRequest request) => SendAsync(new WakeAgentCommand(id, request));
}
