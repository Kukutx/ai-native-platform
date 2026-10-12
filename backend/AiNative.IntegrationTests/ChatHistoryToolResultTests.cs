using System.Net.Http.Json;
using System.Text.Json;
using AiNative.Application.Common;
using AiNative.Contracts.Chat;
using AiNative.IntegrationTests.Infrastructure;

namespace AiNative.IntegrationTests;

/// <summary>
/// 重新打开对话时，历史消息里的工具调用要带上结果对象和状态（与流式推送的 tool_result 一致）。
/// </summary>
public sealed class ChatHistoryToolResultTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Stored_tool_calls_keep_their_result_object_and_status()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        Factory.Script.Handler = (messages, _) => Task.FromResult(ScriptedModel.HasToolResult(messages)
            ? ScriptedModel.Reply("已经建好了。")
            : ScriptedModel.CallTool("tasks_create", new Dictionary<string, object?> { ["title"] = "整理会议纪要" }));
        var conversation = await PostAsync<ConversationDto>(client, "/api/conversations", new CreateConversationRequest());
        var response = await client.PostAsJsonAsync($"/api/conversations/{conversation.Id}/messages", new SendMessageRequest { Text = "建个任务" }, PlatformJson.Options, Ct);
        response.EnsureSuccessStatusCode();
        await response.Content.ReadAsStringAsync(Ct);

        var stored = await GetAsync<List<ChatMessageDto>>(client, $"/api/conversations/{conversation.Id}/messages");

        var toolCall = stored.SelectMany(m => m.ToolCalls).Should().ContainSingle().Subject;
        toolCall.Status.Should().Be("succeeded");
        toolCall.Result!.Value.ValueKind.Should().Be(JsonValueKind.Object);
        toolCall.Result!.Value.GetProperty("output").GetProperty("title").GetString().Should().Be("整理会议纪要");
    }
}
