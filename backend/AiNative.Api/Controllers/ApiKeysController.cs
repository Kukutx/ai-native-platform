using AiNative.Api.Security;
using AiNative.Application.Platform;
using AiNative.Contracts.Platform;
using Microsoft.AspNetCore.Mvc;

namespace AiNative.Api.Controllers;

/// <summary>
/// 个人 API 密钥：给 MCP 客户端（Claude Code、Claude Desktop 等）和脚本用。只能用登录令牌管理，API 密钥本身不能查看、创建或吊销密钥。
/// </summary>
[Route("api/api-keys")]
[DenyApiKey]
public sealed class ApiKeysController : ApiControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<ApiKeyDto>> List()
        => SendAsync(new ListApiKeysQuery());

    [HttpPost]
    public Task<CreatedApiKeyDto> Create(CreateApiKeyRequest request)
        => SendAsync(new CreateApiKeyCommand(request));

    [HttpPost("{id:guid}/revoke")]
    public Task<ApiKeyDto> Revoke(Guid id)
        => SendAsync(new RevokeApiKeyCommand(id));
}
