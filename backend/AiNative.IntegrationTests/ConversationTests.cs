using System.Net;
using System.Net.Http.Json;
using AiNative.Application.Common;
using AiNative.Contracts.Chat;
using AiNative.Contracts.Platform;
using AiNative.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AiNative.IntegrationTests;

/// <summary>
/// 对话属于发起它的成员本人：同一空间的其他成员（包括管理员）既看不到，也不能接着聊或删除。
/// </summary>
public sealed class ConversationTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Other_members_cannot_read_continue_or_delete_someone_elses_conversation()
    {
        var owner = await RegisterAsync();
        using var ownerClient = Client(owner.AccessToken);
        var memberRole = (await GetAsync<List<RoleDto>>(ownerClient, "/api/roles")).Single(r => r.Key == "member");
        using var memberClient = Client(await AddMemberAsync(ownerClient, "member@example.com", memberRole.Id));
        using var colleagueClient = Client(await AddMemberAsync(ownerClient, "colleague@example.com", memberRole.Id));
        var modelCalls = 0;
        Factory.Script.Handler = (_, _) =>
        {
            Interlocked.Increment(ref modelCalls);
            return Task.FromResult(ScriptedModel.Reply("收到。"));
        };
        var conversation = await PostAsync<ConversationDto>(memberClient, "/api/conversations", new CreateConversationRequest());
        (await SendAsync(memberClient, conversation.Id, "下季度的私人计划")).Should().Contain("\"done\"");
        modelCalls.Should().Be(1);

        foreach (var other in new[] { colleagueClient, ownerClient })
        {
            (await GetAsync<List<ConversationDto>>(other, "/api/conversations")).Should().BeEmpty();
            (await other.GetAsync(new Uri($"/api/conversations/{conversation.Id}/messages", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await other.DeleteAsync(new Uri($"/api/conversations/{conversation.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

            // 流式接口要么直接拒绝，要么只推送错误事件；不会调用模型，也不会把消息写进别人的对话。
            var hijack = await other.PostAsJsonAsync($"/api/conversations/{conversation.Id}/messages", new SendMessageRequest { Text = "我也来说一句" }, PlatformJson.Options, Ct);
            if (hijack.IsSuccessStatusCode)
            {
                var body = await hijack.Content.ReadAsStringAsync(Ct);
                body.Should().Contain("\"error\"").And.NotContain("\"done\"");
            }
        }

        modelCalls.Should().Be(1);
        var messages = await GetAsync<List<ChatMessageDto>>(memberClient, $"/api/conversations/{conversation.Id}/messages");
        messages.Select(m => m.Text).Should().Equal("下季度的私人计划", "收到。");
        (await GetAsync<List<ConversationDto>>(memberClient, "/api/conversations")).Should().ContainSingle()
            .Which.Title.Should().Be("下季度的私人计划");
    }

    [Fact]
    public async Task Owner_deletes_a_conversation_together_with_its_messages()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var kept = await PostAsync<ConversationDto>(client, "/api/conversations", new CreateConversationRequest { Title = "保留" });
        var conversation = await PostAsync<ConversationDto>(client, "/api/conversations", new CreateConversationRequest());
        await SendAsync(client, kept.Id, "这条留着");
        await SendAsync(client, conversation.Id, "第一条");
        await SendAsync(client, conversation.Id, "第二条");

        var deleted = await client.DeleteAsync(new Uri($"/api/conversations/{conversation.Id}", UriKind.Relative), Ct);

        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GetAsync<List<ConversationDto>>(client, "/api/conversations")).Should().ContainSingle().Which.Id.Should().Be(kept.Id);
        (await client.GetAsync(new Uri($"/api/conversations/{conversation.Id}/messages", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.DeleteAsync(new Uri($"/api/conversations/{conversation.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var rows = await WithDbAsync(async db => new
        {
            Deleted = await db.ConversationMessages.IgnoreQueryFilters().CountAsync(m => m.ConversationId == conversation.Id, Ct),
            Kept = await db.ConversationMessages.IgnoreQueryFilters().CountAsync(m => m.ConversationId == kept.Id, Ct),
        });
        rows.Should().Be(new { Deleted = 0, Kept = 2 });
    }

    private static async Task<string> SendAsync(HttpClient client, Guid conversationId, string text)
    {
        var response = await client.PostAsJsonAsync($"/api/conversations/{conversationId}/messages", new SendMessageRequest { Text = text }, PlatformJson.Options, Ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(Ct);
    }
}
