using System.Text.Json;

namespace AiNative.Contracts.Chat;

public sealed record ConversationDto(Guid Id, string Title, Guid? AgentId, DateTimeOffset LastMessageAt, DateTimeOffset CreatedAt);

public sealed record CreateConversationRequest
{
    public Guid? AgentId { get; init; }

    public string? Title { get; init; }
}

public sealed record SendMessageRequest
{
    public required string Text { get; init; }
}

public sealed record ToolCallDto(string CallId, string ActionKey, JsonElement Arguments, JsonElement? Result, string? Status);

public sealed record ChatMessageDto(Guid Id, int Seq, string Role, string Text, IReadOnlyList<ToolCallDto> ToolCalls, DateTimeOffset CreatedAt);

/// <summary>
/// 对话流式事件（实时通道推送）：text_delta / tool_call / tool_result / done / error。
/// </summary>
public sealed record ChatStreamEvent(
    string Type,
    string? Text = null,
    string? CallId = null,
    string? ActionKey = null,
    JsonElement? Data = null,
    Guid? MessageId = null);
