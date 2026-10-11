using System.Net;
using System.Net.Http.Json;
using AiNative.Application.Common;
using AiNative.Contracts.Agents;
using AiNative.Contracts.Work;
using AiNative.Domain.Work;
using AiNative.IntegrationTests.Infrastructure;

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
