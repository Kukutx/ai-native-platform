using System.Net.Http.Json;
using System.Text.Json;
using AiNative.Application.Common;
using AiNative.Contracts.Agents;
using AiNative.Contracts.Auth;
using AiNative.Contracts.Chat;
using AiNative.Contracts.Common;
using AiNative.Contracts.Platform;
using AiNative.Contracts.Work;
using AiNative.Domain.Common;
using AiNative.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace AiNative.IntegrationTests;

/// <summary>
/// 工作台对话里的工具调用：以对话成员的身份和权限执行平台动作，过程以 SSE 推送并随消息保存。
/// </summary>
public sealed class ChatToolCallTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Member_creates_a_task_through_a_tool_call_that_is_streamed_and_stored()
    {
        var owner = await RegisterAsync();
        using var ownerClient = Client(owner.AccessToken);
        using var memberClient = Client(await AddMemberAsync(ownerClient, "member@example.com", await RoleIdAsync(ownerClient, "member")));
        var memberId = (await GetAsync<SessionDto>(memberClient, "/api/auth/session")).User.Id;
        Factory.Script.Handler = (messages, _) => Task.FromResult(ScriptedModel.HasToolResult(messages)
            ? ScriptedModel.Reply("已经建好了。")
            : ScriptedModel.CallTool("tasks_create", new Dictionary<string, object?> { ["title"] = "整理会议纪要" }, text: "我来建个任务。"));
        var conversation = await PostAsync<ConversationDto>(memberClient, "/api/conversations", new CreateConversationRequest());

        var events = await SendAsync(memberClient, conversation.Id, "帮我建个任务：整理会议纪要");

        events.Select(e => e.GetProperty("type").GetString()).Where(t => t != "text_delta").Should().Equal("tool_call", "tool_result", "done");
        var call = events.Single(e => e.GetProperty("type").GetString() == "tool_call");
        call.GetProperty("actionKey").GetString().Should().Be("tasks.create");
        call.GetProperty("data").GetProperty("title").GetString().Should().Be("整理会议纪要");
        var result = events.Single(e => e.GetProperty("type").GetString() == "tool_result");
        result.GetProperty("callId").GetString().Should().Be(call.GetProperty("callId").GetString());
        result.GetProperty("data").GetProperty("status").GetString().Should().Be("ok");
        var taskId = result.GetProperty("data").GetProperty("output").GetProperty("id").GetGuid();
        Text(events).Should().Be("我来建个任务。已经建好了。");

        // 任务以成员本人的身份创建。
        var task = (await GetAsync<WorkItemDetailDto>(ownerClient, $"/api/work-items/{taskId}")).Item;
        task.Title.Should().Be("整理会议纪要");
        task.CreatedBy.Should().Match<ActorDto>(a => a.Kind == ActorKind.User && a.Id == memberId);

        // 保存的消息里工具调用挂在发起它的助手消息上，工具消息本身不单独展示。
        var stored = await GetAsync<List<ChatMessageDto>>(memberClient, $"/api/conversations/{conversation.Id}/messages");
        stored.Select(m => (m.Role, m.Text)).Should().Equal(("user", "帮我建个任务：整理会议纪要"), ("assistant", "我来建个任务。"), ("assistant", "已经建好了。"));
        var toolCall = stored[1].ToolCalls.Should().ContainSingle().Subject;
        toolCall.ActionKey.Should().Be("tasks.create");
        toolCall.CallId.Should().Be(call.GetProperty("callId").GetString());
        toolCall.Arguments.GetProperty("title").GetString().Should().Be("整理会议纪要");
        toolCall.Result.Should().NotBeNull();
        stored[2].ToolCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Tool_outside_the_members_permissions_is_not_offered_and_a_forced_call_is_refused()
    {
        var owner = await RegisterAsync();
        using var ownerClient = Client(owner.AccessToken);
        using var memberClient = Client(await AddMemberAsync(ownerClient, "member@example.com", await RoleIdAsync(ownerClient, "member")));
        IReadOnlyList<string> offered = [];
        Factory.Script.Handler = (messages, options) =>
        {
            if (ScriptedModel.HasToolResult(messages))
            {
                return Task.FromResult(ScriptedModel.Reply("抱歉，这件事我做不了。"));
            }

            offered = [.. options!.Tools!.Select(t => t.Name)];
            return Task.FromResult(ScriptedModel.CallTool("agents_hire", new Dictionary<string, object?>
            {
                ["name"] = "小研",
                ["title"] = "研究员",
                ["instructions"] = "负责市场调研。",
            }));
        };
        var conversation = await PostAsync<ConversationDto>(memberClient, "/api/conversations", new CreateConversationRequest());

        var events = await SendAsync(memberClient, conversation.Id, "招一名研究员");

        // 成员角色没有管理智能体的权限：工具列表里没有招聘，模型硬调也只得到拒绝结果，这一轮正常结束。
        offered.Should().Contain("tasks_create").And.NotContain("agents_hire");
        events.Select(e => e.GetProperty("type").GetString()).Should().NotContain("error").And.EndWith("done");
        var result = events.Single(e => e.GetProperty("type").GetString() == "tool_result").GetProperty("data");
        result.GetProperty("status").GetString().Should().NotBe("ok");
        result.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
        Text(events).Should().Be("抱歉，这件事我做不了。");
        (await GetAsync<List<AgentDto>>(ownerClient, "/api/agents")).Should().ContainSingle();
        (await WithDbAsync(db => db.Approvals.IgnoreQueryFilters().CountAsync(Ct))).Should().Be(0);

        var stored = await GetAsync<List<ChatMessageDto>>(memberClient, $"/api/conversations/{conversation.Id}/messages");
        stored.SelectMany(m => m.ToolCalls).Should().ContainSingle()
            .Which.CallId.Should().Be(events.Single(e => e.GetProperty("type").GetString() == "tool_call").GetProperty("callId").GetString());
    }

    [Fact]
    public async Task Action_that_needs_approval_only_when_an_agent_asks_runs_directly_for_a_person()
    {
        var owner = await RegisterAsync();
        using var client = Client(owner.AccessToken);
        Factory.Script.Handler = (messages, _) => Task.FromResult(ScriptedModel.HasToolResult(messages)
            ? ScriptedModel.Reply("小研已经入职。")
            : ScriptedModel.CallTool("agents_hire", new Dictionary<string, object?>
            {
                ["name"] = "小研",
                ["title"] = "研究员",
                ["instructions"] = "负责市场调研。",
            }));
        var conversation = await PostAsync<ConversationDto>(client, "/api/conversations", new CreateConversationRequest());

        var events = await SendAsync(client, conversation.Id, "招一名研究员");

        // agents.hire 只在智能体发起时需要审批；对话以成员身份执行，直接生效、不产生审批单。
        var result = events.Single(e => e.GetProperty("type").GetString() == "tool_result").GetProperty("data");
        result.GetProperty("status").GetString().Should().Be("ok");
        var hiredId = result.GetProperty("output").GetProperty("id").GetGuid();
        var hired = (await GetAsync<List<AgentDto>>(client, "/api/agents")).Should().ContainSingle(a => a.Id == hiredId).Subject;
        hired.Name.Should().Be("小研");
        hired.ReportsToId.Should().BeNull();
        (await WithDbAsync(db => db.Approvals.IgnoreQueryFilters().CountAsync(Ct))).Should().Be(0);
        events.Select(e => e.GetProperty("type").GetString()).Should().EndWith("done");
    }

    private static async Task<Guid> RoleIdAsync(HttpClient ownerClient, string key)
        => (await GetAsync<List<RoleDto>>(ownerClient, "/api/roles")).Single(r => r.Key == key).Id;

    /// <summary>
    /// 发送一条消息，读完整个 SSE 响应，按顺序返回每个事件的 JSON。
    /// </summary>
    private static async Task<List<JsonElement>> SendAsync(HttpClient client, Guid conversationId, string text)
    {
        var response = await client.PostAsJsonAsync($"/api/conversations/{conversationId}/messages", new SendMessageRequest { Text = text }, PlatformJson.Options, Ct);
        response.EnsureSuccessStatusCode();
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        var body = await response.Content.ReadAsStringAsync(Ct);
        return [.. body.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal)).Select(line => Json(line["data:".Length..].Trim()))];
    }

    private static string Text(IEnumerable<JsonElement> events)
        => string.Concat(events.Where(e => e.GetProperty("type").GetString() == "text_delta").Select(e => e.GetProperty("text").GetString()));
}
