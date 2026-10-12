using System.Text.Json;
using AiNative.Application.Actions;
using AiNative.Contracts.Actions;
using Microsoft.AspNetCore.Mvc;

namespace AiNative.Api.Controllers;

/// <summary>
/// 平台动作的通用入口：界面、智能体、第三方集成调用的是同一份动作定义。
/// </summary>
[Route("api/actions")]
public sealed class ActionsController : ApiControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<ActionDescriptorDto>> List() => SendAsync(new ListActionsQuery());

    [HttpPost("{key}/invoke")]
    public Task<InvokeActionResponse> Invoke(string key, [FromBody] JsonElement input) => SendAsync(new InvokeActionCommand(key, input));
}
