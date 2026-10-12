using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace AiNative.Infrastructure.Ai;

/// <summary>
/// 内置演示模型：不联网、不收费。没有配置真实模型时也能走通"指派任务 → 智能体被唤醒 → 评论 → 总结"的完整链路，
/// 方便本地体验和端到端测试。
/// </summary>
internal sealed partial class DemoChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(Respond([.. messages], options));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = Respond([.. messages], options);
        foreach (var update in response.ToChatResponseUpdates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private static ChatResponse Respond(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var inputChars = messages.Sum(m => m.Text?.Length ?? 0);
        var last = messages[^1];
        var toolNames = options?.Tools?.Select(t => t.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        ChatMessage reply;

        if (last.Role == ChatRole.Tool)
        {
            reply = new ChatMessage(ChatRole.Assistant, "（演示模型）已在任务下留言说明情况。接入真实模型后，我会真正开始处理这项工作。");
        }
        else if (toolNames.Contains("tasks_comment") && FindFocusTask(messages) is { } taskId)
        {
            reply = new ChatMessage(ChatRole.Assistant,
            [
                new TextContent("（演示模型）收到任务，先在任务下留言。"),
                new FunctionCallContent($"demo-{Guid.NewGuid():N}", "tasks_comment", new Dictionary<string, object?>
                {
                    ["taskId"] = taskId,
                    ["body"] = "（演示模型）我已收到这个任务。当前空间还没有配置真实的模型提供方，所以暂时无法真正执行。管理员在 appsettings 的 Ai:Providers 中配置 Anthropic 或 OpenAI 兼容的 API Key 后，我就会按岗位职责开始工作。",
                }),
            ]);
        }
        else
        {
            var question = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
            reply = new ChatMessage(ChatRole.Assistant,
                $"这是演示模型的回复：当前还没有配置真实的模型提供方，在 appsettings 的 Ai:Providers 中填写 Anthropic 或 OpenAI 兼容的 API Key 即可获得真实回答。\n\n你刚才说的是：{question}");
        }

        return new ChatResponse(reply)
        {
            ModelId = "demo",
            FinishReason = reply.Contents.OfType<FunctionCallContent>().Any() ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
            Usage = new UsageDetails { InputTokenCount = inputChars / 4, OutputTokenCount = (reply.Text?.Length ?? 0) / 4 },
        };
    }

    private static string? FindFocusTask(IReadOnlyList<ChatMessage> messages)
    {
        // 本轮已经调用过工具就不再重复评论。
        if (messages.Any(m => m.Role == ChatRole.Tool))
        {
            return null;
        }

        foreach (var message in messages.Where(m => m.Role == ChatRole.User))
        {
            var match = FocusPattern().Match(message.Text ?? string.Empty);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    [GeneratedRegex(@"focus_task_id:\s*([0-9a-fA-F-]{36})")]
    private static partial Regex FocusPattern();
}
