using AiNative.Application.Integrations;
using AiNative.Contracts.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace AiNative.Api.Controllers;

[Route("api/integrations")]
public sealed class IntegrationsController : ApiControllerBase
{
    /// <summary>
    /// 接入的外部 MCP 服务及其工具。
    /// </summary>
    [HttpGet("mcp-servers")]
    public Task<IReadOnlyList<McpServerDto>> McpServers() => SendAsync(new ListMcpServersQuery());

    [HttpGet("mcp-servers/{id:guid}")]
    public Task<McpServerDto> GetMcpServer(Guid id) => SendAsync(new GetMcpServerQuery(id));

    /// <summary>
    /// 接入外部 MCP 服务。保存时会连接它读取工具列表；连不上也会保存，原因见 lastError。
    /// </summary>
    [HttpPost("mcp-servers")]
    public Task<McpServerDto> CreateMcpServer(SaveMcpServerRequest request) => SendAsync(new CreateMcpServerCommand(request));

    [HttpPut("mcp-servers/{id:guid}")]
    public Task<McpServerDto> UpdateMcpServer(Guid id, SaveMcpServerRequest request) => SendAsync(new UpdateMcpServerCommand(id, request));

    /// <summary>
    /// 重新读取工具列表。
    /// </summary>
    [HttpPost("mcp-servers/{id:guid}/refresh")]
    public Task<McpServerDto> RefreshMcpServer(Guid id) => SendAsync(new RefreshMcpServerToolsCommand(id));

    /// <summary>
    /// 调整一个工具：开关、确认只读。只改给出的项。
    /// </summary>
    [HttpPut("mcp-servers/{id:guid}/tools")]
    public Task<McpServerDto> UpdateMcpTool(Guid id, UpdateMcpToolRequest request) => SendAsync(new UpdateMcpToolCommand(id, request));

    [HttpDelete("mcp-servers/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteMcpServer(Guid id)
    {
        await SendAsync(new DeleteMcpServerCommand(id)).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// 密钥列表（只有名称和说明，拿不到值）。
    /// </summary>
    [HttpGet("secrets")]
    public Task<IReadOnlyList<TenantSecretDto>> Secrets() => SendAsync(new ListTenantSecretsQuery());

    [HttpPost("secrets")]
    public Task<TenantSecretDto> CreateSecret(CreateTenantSecretRequest request) => SendAsync(new CreateTenantSecretCommand(request));

    /// <summary>
    /// 更换密钥的值或修改说明。不传 value 表示只改说明。
    /// </summary>
    [HttpPut("secrets/{id:guid}")]
    public Task<TenantSecretDto> UpdateSecret(Guid id, UpdateTenantSecretRequest request) => SendAsync(new UpdateTenantSecretCommand(id, request));

    /// <summary>
    /// 删除密钥。还被外部服务或工作流引用时返回 409。
    /// </summary>
    [HttpDelete("secrets/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteSecret(Guid id)
    {
        await SendAsync(new DeleteTenantSecretCommand(id)).ConfigureAwait(false);
        return NoContent();
    }
}
