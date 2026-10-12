using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AiNative.Infrastructure.Ai;
using Microsoft.Extensions.AI;

namespace AiNative.IntegrationTests.Infrastructure;

/// <summary>
/// 测试用的可编排模型：每次请求交给 <see cref="Handler"/> 决定回复什么。
/// </summary>
public sealed partial class ScriptedModel
{
    public Func<IReadOnlyList<ChatMessage>, ChatOptions?, Task<ChatResponse>> Handler { get; set; }
        = (_, _) => Task.FromResult(Reply("好的。"));

    public static ChatResponse Reply(string text, long inputTokens = 100, long outputTokens = 20) => new(new ChatMessage(ChatRole.Assistant, text))
    {
        FinishReason = ChatFinishReason.Stop,
        Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens },
    };

    public static ChatResponse CallTool(string tool, IDictionary<string, object?> arguments, string? text = null, long inputTokens = 100, long outputTokens = 20)
    {
        List<AIContent> contents = [];
        if (text is not null)
        {
            contents.Add(new TextContent(text));
        }

        contents.Add(new FunctionCallContent($"call-{Guid.NewGuid():N}", tool, arguments));
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
        {
            FinishReason = ChatFinishReason.ToolCalls,
            Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens },
        };
    }

    public static bool HasToolResult(IReadOnlyList<ChatMessage> messages) => messages.Any(m => m.Role == ChatRole.Tool);

    public static string? FocusTaskId(IReadOnlyList<ChatMessage> messages)
        => messages.Where(m => m.Role == ChatRole.User).Select(m => FocusPattern().Match(m.Text ?? string.Empty)).FirstOrDefault(m => m.Success)?.Groups[1].Value;

    [GeneratedRegex(@"focus_task_id:\s*([0-9a-fA-F-]{36})")]
    private static partial Regex FocusPattern();
}

internal sealed class ScriptedChatClientFactory(ScriptedModel model) : IChatClientFactory
{
    public string Kind => "scripted";

    public bool IsConfigured(ModelProviderOptions options) => true;

    public IChatClient Create(ModelProviderOptions options, string modelId) => new ScriptedChatClient(model);
}

internal sealed class ScriptedChatClient(ScriptedModel model) : IChatClient
{
    // 与真实模型客户端一样响应取消：服务停止、租约失效时卡住的脚本不会拖住执行。
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => model.Handler([.. messages], options).WaitAsync(cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await model.Handler([.. messages], options).WaitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
