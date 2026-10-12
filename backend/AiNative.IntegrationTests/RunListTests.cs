using System.Net;
using AiNative.Contracts.Agents;
using AiNative.Contracts.Common;
using AiNative.Contracts.Platform;
using AiNative.Domain.Agents;
using AiNative.IntegrationTests.Infrastructure;

namespace AiNative.IntegrationTests;

/// <summary>
/// 运行记录列表：最新的在前，可以按智能体筛选、分页，只列本空间的运行。
/// </summary>
public sealed class RunListTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Runs_are_listed_newest_first_filtered_by_agent_and_paged()
    {
        Factory.Script.Handler = (_, _) => Task.FromResult(ScriptedModel.Reply("看过了。"));
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var chief = await StarterAgentAsync(client);
        var analyst = await PostAsync<AgentDto>(client, "/api/agents", new SaveAgentRequest { Name = "小李", Title = "分析师" });

        // 逐个唤醒、等上一次跑完再唤醒下一次，避免合并成一次运行。
        foreach (var agentId in new[] { chief.Id, analyst.Id, chief.Id })
        {
            var before = (await RunsAsync()).Count;
            await PostAsync<WakeupDto>(client, $"/api/agents/{agentId}/wake", new WakeAgentRequest { Reason = "看看进度" });
            await EventuallyAsync(RunsAsync, r => r.Count == before + 1 && r.All(x => x.Status == RunStatus.Succeeded));
        }

        var all = await GetAsync<PagedResult<RunDto>>(client, "/api/runs");
        all.Total.Should().Be(3);
        all.Items.Select(r => r.AgentId).Should().Equal(chief.Id, analyst.Id, chief.Id);
        all.Items.Should().BeInDescendingOrder(r => r.CreatedAt);
        all.Items.Should().OnlyContain(r => r.Status == RunStatus.Succeeded && r.Summary == "看过了。");

        var byAnalyst = await GetAsync<PagedResult<RunDto>>(client, $"/api/runs?agentId={analyst.Id}");
        byAnalyst.Total.Should().Be(1);
        byAnalyst.Items.Should().ContainSingle().Which.AgentName.Should().Be("小李");
        (await GetAsync<PagedResult<RunDto>>(client, $"/api/runs?agentId={chief.Id}")).Total.Should().Be(2);

        var second = await GetAsync<PagedResult<RunDto>>(client, "/api/runs?page=2&pageSize=2");
        (second.Total, second.Page, second.PageSize).Should().Be((3, 2, 2));
        second.Items.Should().ContainSingle().Which.Id.Should().Be(all.Items[2].Id);

        // 没有查看运行权限的角色看不到；别的空间的列表是空的。
        var tasksOnly = await PostAsync<RoleDto>(client, "/api/roles", new SaveRoleRequest { Key = "tasks-only", Name = "只管任务", Features = ["tasks.view"] });
        using var tasksOnlyClient = Client(await AddMemberAsync(client, "tasks@example.com", tasksOnly.Id));
        (await tasksOnlyClient.GetAsync(new Uri("/api/runs", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var other = await RegisterAsync("other@example.com", "别的空间");
        using var otherClient = Client(other.AccessToken);
        (await GetAsync<PagedResult<RunDto>>(otherClient, "/api/runs")).Total.Should().Be(0);
    }
}
