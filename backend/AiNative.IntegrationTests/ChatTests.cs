using System.Net.Http.Json;
using AiNative.Application.Common;
using AiNative.Contracts.Chat;
using AiNative.IntegrationTests.Infrastructure;
using Microsoft.Extensions.AI;

namespace AiNative.IntegrationTests;

public sealed class ChatTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Second_message_is_rejected_while_the_previous_turn_is_still_replying()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var conversation = await PostAsync<ConversationDto>(client, "/api/conversations", new CreateConversationRequest());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Factory.Script.Handler = async (messages, _) =>
        {
            if (messages[^1].Text == "第一条")
            {
                entered.TrySetResult();
                await release.Task;
            }

            return ScriptedModel.Reply("收到。");
        };

        var first = SendAsync(client, conversation.Id, "第一条");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        var second = await SendAsync(client, conversation.Id, "第二条");
        release.SetResult();
        var firstBody = await first;

        second.Should().Contain("上一条消息还在回复中");
        firstBody.Should().Contain("\"done\"");
        var messages = await GetAsync<List<ChatMessageDto>>(client, $"/api/conversations/{conversation.Id}/messages");
        messages.Select(m => m.Text).Should().Equal("第一条", "收到。");
        messages.Select(m => m.Seq).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Long_conversations_send_only_recent_history_to_the_model()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var conversation = await PostAsync<ConversationDto>(client, "/api/conversations", new CreateConversationRequest());
        IReadOnlyList<ChatMessage> lastRequest = [];
        Factory.Script.Handler = (messages, _) =>
        {
            lastRequest = messages;
            return Task.FromResult(ScriptedModel.Reply("收到。"));
        };

        for (var i = 1; i <= 36; i++)
        {
            (await SendAsync(client, conversation.Id, $"消息 {i}")).Should().Contain("\"done\"");
        }

        // 系统提示 + 最近 60 条历史（从用户消息开始）+ 本轮消息。
        lastRequest.Should().HaveCount(62);
        lastRequest[1].Role.Should().Be(ChatRole.User);
        lastRequest[1].Text.Should().Be("消息 6");
        lastRequest[^1].Text.Should().Be("消息 36");
        var stored = await GetAsync<List<ChatMessageDto>>(client, $"/api/conversations/{conversation.Id}/messages");
        stored.Should().HaveCount(72);
        stored[^1].Seq.Should().Be(72);
    }

    private static async Task<string> SendAsync(HttpClient client, Guid conversationId, string text)
    {
        var response = await client.PostAsJsonAsync($"/api/conversations/{conversationId}/messages", new SendMessageRequest { Text = text }, PlatformJson.Options, Ct);
        response.EnsureSuccessStatusCode();
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        response.Headers.GetValues("X-Accel-Buffering").Should().Equal("no");
        return await response.Content.ReadAsStringAsync(Ct);
    }
}
