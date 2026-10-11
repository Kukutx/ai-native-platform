using AiNative.Domain.Common;

namespace AiNative.Domain.Chat;

/// <summary>
/// 工作台对话。可以和通用助手聊，也可以直接和某个智能体员工聊。
/// </summary>
public sealed class Conversation : TenantEntity
{
    public Guid UserId { get; set; }

    public Guid? AgentId { get; set; }

    public string Title { get; set; } = "新对话";

    public DateTimeOffset LastMessageAt { get; set; }
}

/// <summary>
/// 对话消息。ContentJson 保存模型消息的完整结构（含工具调用与推理签名），
/// 下一轮原样回放，保证多轮工具调用和推理块在模型侧有效；Text 只用于展示与检索。
/// </summary>
public sealed class ConversationMessage : TenantEntity
{
    public Guid ConversationId { get; set; }

    public int Seq { get; set; }

    public required string Role { get; set; }

    public string ContentJson { get; set; } = "{}";

    public string Text { get; set; } = string.Empty;
}
