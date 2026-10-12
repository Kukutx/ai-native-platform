using System.Net;
using System.Net.Http.Json;
using AiNative.Application.Abstractions;
using AiNative.Application.Agents;
using AiNative.Application.Agents.Runtime;
using AiNative.Application.Common;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Actions;
using AiNative.Contracts.Agents;
using AiNative.Contracts.Chat;
using AiNative.Contracts.Common;
using AiNative.Contracts.Projects;
using AiNative.Contracts.Work;
using AiNative.Domain.Agents;
using AiNative.Domain.Common;
using AiNative.Domain.Work;
using AiNative.Infrastructure.Persistence;
using AiNative.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiNative.IntegrationTests;

/// <summary>
/// 健壮性：并发关闭任务不丢唤醒、输入超长返回 400、唤醒原因有上限、超大页码、任务搜索。
/// </summary>
public sealed class RobustnessTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Closing_the_last_predecessors_concurrently_still_wakes_the_dependent()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var agent = await StarterAgentAsync(client);
        await PostAsync<AgentDto>(client, $"/api/agents/{agent.Id}/pause", new { });

        for (var round = 0; round < 5; round++)
        {
            var first = await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = $"前置甲 {round}" });
            var second = await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = $"前置乙 {round}" });
            var next = await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest
            {
                Title = $"后续 {round}",
                AssigneeAgentId = agent.Id,
                DependsOn = [first.Id, second.Id],
            });

            // 两个前置任务同时关闭：依赖锁让后提交的一方看到先提交的关闭，后续任务的负责人一定会被唤醒。
            await Task.WhenAll(
                InvokeAsync(client, "tasks.update_status", new { taskId = first.Id, status = WorkItemStatus.Done }),
                InvokeAsync(client, "tasks.update_status", new { taskId = second.Id, status = WorkItemStatus.Cancelled }));

            (await WakeItemsAsync(agent.Id)).Should().Contain(i => i.WorkItemId == next.Id && i.Source == WakeupSource.Dependency, $"第 {round} 轮");
        }
    }

    [Fact]
    public async Task Closing_the_last_project_tasks_concurrently_notifies_the_lead()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var lead = await StarterAgentAsync(client);
        await PostAsync<AgentDto>(client, $"/api/agents/{lead.Id}/pause", new { });

        for (var round = 0; round < 4; round++)
        {
            var project = (await PostAsync<ProjectDetailDto>(client, "/api/projects", new SaveProjectRequest { Name = $"并发收尾 {round}", LeadAgentId = lead.Id })).Project;
            var first = await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "甲", ProjectId = project.Id });
            var second = await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "乙", ProjectId = project.Id });

            await Task.WhenAll(
                InvokeAsync(client, "tasks.update_status", new { taskId = first.Id, status = WorkItemStatus.Done }),
                InvokeAsync(client, "tasks.update_status", new { taskId = second.Id, status = WorkItemStatus.Done }));

            (await WakeItemsAsync(lead.Id)).Should().Contain(i => i.Reason.Contains($"项目《{project.Name}》的任务都已关闭"), $"第 {round} 轮");
        }
    }

    [Fact]
    public async Task Overlong_input_is_rejected_before_reaching_the_database()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var agent = await StarterAgentAsync(client);
        var task = await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "长输入" });

        var conversation = await client.PostAsJsonAsync("/api/conversations", new CreateConversationRequest { Title = new string('长', 201) }, PlatformJson.Options, Ct);
        var wake = await client.PostAsJsonAsync($"/api/agents/{agent.Id}/wake", new WakeAgentRequest { Reason = new string('因', 2001) }, PlatformJson.Options, Ct);
        var status = await PostAsync<InvokeActionResponse>(
            client, "/api/actions/tasks.update_status/invoke", new { taskId = task.Id, status = WorkItemStatus.Done, comment = new string('评', 20_001) });

        foreach (var response in new[] { conversation, wake })
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        }

        status.Status.Should().NotBe("succeeded");

        (await GetAsync<WorkItemDetailDto>(client, $"/api/work-items/{task.Id}")).Item.Status.Should().Be(WorkItemStatus.Todo);
    }

    [Fact]
    public async Task Wakeup_reasons_are_capped_and_the_prompt_counts_the_rest()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var agent = await StarterAgentAsync(client);
        await PostAsync<AgentDto>(client, $"/api/agents/{agent.Id}/pause", new { });
        var tenantId = auth.Session.Tenant.Id;

        // 不在事务里入队（和审批决定后的通知一样），队列自己开事务。
        for (var i = 1; i <= WakeupRequest.MaxItems + 10; i++)
        {
            var reason = $"第 {i} 次提醒";
            await Factory.Services.GetRequiredService<ITenantScopeFactory>().RunAsync(tenantId, Actor.System, [], sp =>
                sp.GetRequiredService<IWakeupQueue>().EnqueueAsync(agent.Id, new WakeupItem { Source = WakeupSource.OnDemand, Reason = reason }, Actor.System, cancellationToken: Ct));
        }

        var queued = await WithDbAsync(db => db.WakeupRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.AgentId == agent.Id && w.Status == WakeupStatus.Queued, Ct));
        queued.Items.Should().HaveCount(WakeupRequest.MaxItems);
        queued.Items[0].Reason.Should().Be("第 11 次提醒");
        queued.Items[^1].Reason.Should().Be($"第 {WakeupRequest.MaxItems + 10} 次提醒");
        queued.CoalescedCount.Should().Be(WakeupRequest.MaxItems + 9);

        var prompt = await Factory.Services.GetRequiredService<ITenantScopeFactory>().RunAsync(tenantId, Actor.System, [], async sp =>
        {
            var loaded = await sp.GetRequiredService<AppDbContext>().Agents.SingleAsync(a => a.Id == agent.Id, Ct);
            return await sp.GetRequiredService<AgentPromptBuilder>().BuildWakeMessageAsync(loaded, queued, null, false, Ct);
        });
        prompt.Should().Contain($"累计了 {WakeupRequest.MaxItems + 10} 次唤醒").And.Contain("更早的 10 条已省略").And.NotContain("第 10 次提醒");
    }

    [Fact]
    public async Task Huge_page_numbers_return_an_empty_page()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "唯一的任务" });

        foreach (var url in new[]
                 {
                     $"/api/work-items?page={int.MaxValue}&pageSize=200",
                     $"/api/runs?page={int.MaxValue}&pageSize=100",
                     $"/api/crm/contacts?page={int.MaxValue}&pageSize=200",
                     $"/api/projects?page={int.MaxValue}&pageSize=200",
                 })
        {
            var response = await client.GetAsync(new Uri(url, UriKind.Relative), Ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, url);
            var page = await response.Content.ReadFromJsonAsync<PagedResult<System.Text.Json.JsonElement>>(PlatformJson.Options, Ct);
            page!.Items.Should().BeEmpty(url);
        }
    }

    [Fact]
    public async Task Task_search_matches_keys_and_treats_wildcards_literally()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var done = await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "进度 100% 的汇报" });
        await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "普通任务", Description = "下划线_说明" });

        (await SearchAsync(client, "%")).Should().ContainSingle().Which.Id.Should().Be(done.Id);
        (await SearchAsync(client, "_")).Should().ContainSingle().Which.Title.Should().Be("普通任务");
        (await SearchAsync(client, done.Key)).Should().ContainSingle().Which.Id.Should().Be(done.Id);
        (await SearchAsync(client, done.Key.ToLowerInvariant())).Should().ContainSingle().Which.Id.Should().Be(done.Id);
        (await SearchAsync(client, "汇报")).Should().ContainSingle().Which.Id.Should().Be(done.Id);
    }

    private static async Task<IReadOnlyList<WorkItemDto>> SearchAsync(HttpClient client, string term)
        => (await GetAsync<PagedResult<WorkItemDto>>(client, $"/api/work-items?search={Uri.EscapeDataString(term)}")).Items;

    private async Task<List<WakeupItem>> WakeItemsAsync(Guid agentId)
        => (await WithDbAsync(db => db.WakeupRequests.IgnoreQueryFilters().AsNoTracking().Where(w => w.AgentId == agentId).ToListAsync(Ct)))
            .SelectMany(w => w.Items).ToList();
}
