using System.Net;
using System.Net.Http.Json;
using AiNative.Application.Common;
using AiNative.Contracts.Agents;
using AiNative.Contracts.Common;
using AiNative.Contracts.Platform;
using AiNative.Contracts.Work;
using AiNative.Domain.Agents;
using AiNative.Domain.Common;
using AiNative.Domain.Work;
using AiNative.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AiNative.IntegrationTests;

public sealed class WorkItemTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Edit_based_on_stale_data_is_rejected_instead_of_overwriting()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var created = await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "写周报" });

        var first = await PutAsync<WorkItemDto>(client, $"/api/work-items/{created.Id}", Edit(created) with { Priority = WorkItemPriority.High });
        var current = await GetAsync<WorkItemDetailDto>(client, $"/api/work-items/{created.Id}");
        var stale = await client.PutAsJsonAsync($"/api/work-items/{created.Id}", Edit(created) with { Title = "旧标题覆盖" }, PlatformJson.Options, Ct);
        var fresh = await PutAsync<WorkItemDto>(client, $"/api/work-items/{created.Id}", Edit(current.Item) with { Title = "写月报" });

        first.Priority.Should().Be(WorkItemPriority.High);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await stale.Content.ReadAsStringAsync(Ct)).Should().Contain("work_item.modified");
        fresh.Title.Should().Be("写月报");
        fresh.Priority.Should().Be(WorkItemPriority.High);
    }

    [Fact]
    public async Task Closed_task_cannot_be_reassigned_to_a_terminated_agent()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var agent = await StarterAgentAsync(client);
        var task = await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "已完成的任务", Status = WorkItemStatus.Done });
        await PostAsync<AgentDto>(client, $"/api/agents/{agent.Id}/terminate", new { });

        var response = await client.PutAsJsonAsync($"/api/work-items/{task.Id}", Edit(task) with { AssigneeAgentId = agent.Id }, PlatformJson.Options, Ct);

        response.IsSuccessStatusCode.Should().BeFalse();
        (await GetAsync<WorkItemDetailDto>(client, $"/api/work-items/{task.Id}")).Item.AssigneeAgentId.Should().BeNull();
    }

    [Fact]
    public async Task Comments_show_on_the_task_and_wake_its_agent_until_the_task_is_closed()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var agent = await StarterAgentAsync(client);

        // 先暂停智能体，唤醒留在队列里便于检查。
        await PostAsync<AgentDto>(client, $"/api/agents/{agent.Id}/pause", new { });
        var task = await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "写周报", AssigneeAgentId = agent.Id });
        var url = $"/api/work-items/{task.Id}/comments";
        var longBody = new string('长', 200);

        var first = await PostAsync<CommentDto>(client, url, new AddCommentRequest { Body = "请先列提纲" });
        await PostAsync<CommentDto>(client, url, new AddCommentRequest { Body = longBody });

        first.Should().Match<CommentDto>(c => c.WorkItemId == task.Id && c.Body == "请先列提纲" && c.RunId == null);
        first.Author.Should().Match<ActorDto>(a => a.Kind == ActorKind.User && a.Id == auth.Session.User.Id);
        var detail = await GetAsync<WorkItemDetailDto>(client, $"/api/work-items/{task.Id}");
        detail.Comments.Select(c => c.Body).Should().Equal("请先列提纲", longBody);

        // 指派的智能体被评论唤醒，原因里带任务编号和评论摘要（过长的截断）。
        var wakeups = await CommentWakeupsAsync(agent.Id);
        wakeups.Should().HaveCount(2).And.OnlyContain(i => i.WorkItemId == task.Id);
        wakeups[0].Reason.Should().Be($"任务 {task.Key} 有新评论：请先列提纲");
        wakeups[1].Reason.Should().Be($"任务 {task.Key} 有新评论：{new string('长', 120)}…");

        // 只读成员不能评论，空评论被拒绝，别的空间找不到这个任务。
        var viewer = (await GetAsync<List<RoleDto>>(client, "/api/roles")).Single(r => r.Key == "viewer");
        using var viewerClient = Client(await AddMemberAsync(client, "viewer@example.com", viewer.Id));
        (await viewerClient.PostAsJsonAsync(url, new AddCommentRequest { Body = "围观" }, PlatformJson.Options, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync(url, new AddCommentRequest { Body = " " }, PlatformJson.Options, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var other = await RegisterAsync("other@example.com", "别的空间");
        using var otherClient = Client(other.AccessToken);
        (await otherClient.PostAsJsonAsync(url, new AddCommentRequest { Body = "路过" }, PlatformJson.Options, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 任务关闭后的评论照常记录，但不再唤醒智能体。
        await PutAsync<WorkItemDto>(client, $"/api/work-items/{task.Id}", Edit(detail.Item) with { Status = WorkItemStatus.Done });
        await PostAsync<CommentDto>(client, url, new AddCommentRequest { Body = "辛苦了" });
        (await GetAsync<WorkItemDetailDto>(client, $"/api/work-items/{task.Id}")).Comments.Should().Contain(c => c.Body == "辛苦了")
            .And.NotContain(c => c.Body == "围观" || c.Body == "路过");
        (await CommentWakeupsAsync(agent.Id)).Should().HaveCount(2);
    }

    private Task<List<WakeupItem>> CommentWakeupsAsync(Guid agentId) => WithDbAsync(async db =>
        (await db.WakeupRequests.IgnoreQueryFilters().AsNoTracking().Where(w => w.AgentId == agentId).ToListAsync(Ct))
            .SelectMany(w => w.Items).Where(i => i.Source == WakeupSource.Comment).OrderBy(i => i.RequestedAt).ToList());

    private static UpdateWorkItemRequest Edit(WorkItemDto item) => new()
    {
        Title = item.Title,
        Description = item.Description,
        Status = item.Status,
        Priority = item.Priority,
        AssigneeAgentId = item.AssigneeAgentId,
        AssigneeUserId = item.AssigneeUserId,
        ExpectedUpdatedAt = item.UpdatedAt,
    };
}
