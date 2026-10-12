using AiNative.Application.Chat;
using AiNative.Contracts.Chat;
using Microsoft.AspNetCore.Mvc;

namespace AiNative.Api.Controllers;

[Route("api/conversations")]
public sealed class ConversationsController(IChatService chat) : ApiControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<ConversationDto>> List() => SendAsync(new ListConversationsQuery());

    [HttpPost]
    public Task<ConversationDto> Create(CreateConversationRequest request) => SendAsync(new CreateConversationCommand(request));

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await SendAsync(new DeleteConversationCommand(id)).ConfigureAwait(false);
        return NoContent();
    }

    [HttpGet("{id:guid}/messages")]
    public Task<IReadOnlyList<ChatMessageDto>> Messages(Guid id) => SendAsync(new GetConversationMessagesQuery(id));

    /// <summary>
    /// 发送消息并以 Server-Sent Events 流式返回（实时通道 StreamChat 是等价的 WebSocket 版本）。
    /// 流式接口不进 OpenAPI 生成的客户端，由 @ainative/api-client 的 streamChat 手写封装。
    /// </summary>
    [HttpPost("{id:guid}/messages")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public IResult Send(Guid id, SendMessageRequest request)
        => TypedResults.ServerSentEvents(chat.SendAsync(id, request.Text, HttpContext.RequestAborted));
}
