using System.Net;
using System.Net.Http.Json;
using AiNative.Application.Common;
using AiNative.Contracts.Agents;
using AiNative.Contracts.Common;
using AiNative.Contracts.Governance;
using AiNative.Domain.Agents;
using AiNative.Domain.Governance;
using AiNative.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AiNative.IntegrationTests;

/// <summary>
/// 按 ID 直接访问别的空间的智能体、运行记录和审批单：一律当作不存在（404），也不留下任何副作用。
/// </summary>
public sealed class CrossTenantTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Agents_runs_and_approvals_of_another_space_are_not_found_by_id()
    {
        Factory.Script.Handler = (messages, _) => Task.FromResult(ScriptedModel.HasToolResult(messages)
            ? ScriptedModel.Reply("已提交招聘申请。")
            : ScriptedModel.CallTool("agents_hire", new Dictionary<string, object?>
            {
                ["name"] = "小研",
                ["title"] = "研究员",
                ["instructions"] = "负责市场调研。",
            }));
        var alice = await RegisterAsync("alice@example.com", "A 公司");
        using var aliceClient = Client(alice.AccessToken);
        var agent = await StarterAgentAsync(aliceClient);
        await PostAsync<WakeupDto>(aliceClient, $"/api/agents/{agent.Id}/wake", new WakeAgentRequest { Reason = "组建团队" });
        var run = (await EventuallyAsync(RunsAsync, r => r.Count == 1 && r[0].Status == RunStatus.Succeeded)).Single();
        var approval = (await GetAsync<List<ApprovalDto>>(aliceClient, "/api/approvals?status=pending")).Should().ContainSingle().Subject;
        await WaitForIdleAsync();

        var bob = await RegisterAsync("bob@example.com", "B 公司");
        using var bobClient = Client(bob.AccessToken);
        var attempts = new Dictionary<string, HttpResponseMessage>
        {
            ["查看智能体"] = await bobClient.GetAsync(new Uri($"/api/agents/{agent.Id}", UriKind.Relative), Ct),
            ["修改智能体"] = await bobClient.PutAsJsonAsync($"/api/agents/{agent.Id}", new SaveAgentRequest { Name = "被改名", Title = "间谍" }, PlatformJson.Options, Ct),
            ["暂停"] = await bobClient.PostAsJsonAsync($"/api/agents/{agent.Id}/pause", new { }, PlatformJson.Options, Ct),
            ["恢复"] = await bobClient.PostAsJsonAsync($"/api/agents/{agent.Id}/resume", new { }, PlatformJson.Options, Ct),
            ["唤醒"] = await bobClient.PostAsJsonAsync($"/api/agents/{agent.Id}/wake", new WakeAgentRequest { Reason = "替我干活" }, PlatformJson.Options, Ct),
            ["终止"] = await bobClient.PostAsJsonAsync($"/api/agents/{agent.Id}/terminate", new { }, PlatformJson.Options, Ct),
            ["查看运行"] = await bobClient.GetAsync(new Uri($"/api/runs/{run.Id}", UriKind.Relative), Ct),
            ["审批"] = await bobClient.PostAsJsonAsync($"/api/approvals/{approval.Id}/decision", new DecideApprovalRequest { Approve = true, Note = "替你批了" }, PlatformJson.Options, Ct),
        };

        foreach (var (name, response) in attempts)
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, name);
        }

        (await GetAsync<PagedResult<RunDto>>(bobClient, $"/api/runs?agentId={agent.Id}")).Total.Should().Be(0);
        (await GetAsync<List<ApprovalDto>>(bobClient, "/api/approvals")).Should().BeEmpty();

        // 原空间的数据一点没变：没有被改名、暂停或终止，审批单仍待处理，也没有多出唤醒和运行。
        var unchanged = await GetAsync<AgentDto>(aliceClient, $"/api/agents/{agent.Id}");
        (unchanged.Name, unchanged.Title, unchanged.Status).Should().Be((agent.Name, agent.Title, AgentStatus.Idle));
        (await GetAsync<List<ApprovalDto>>(aliceClient, "/api/approvals")).Should().ContainSingle()
            .Which.Should().Match<ApprovalDto>(a => a.Id == approval.Id && a.Status == ApprovalStatus.Pending && a.DecidedByUserId == null);
        (await GetAsync<List<AgentDto>>(aliceClient, "/api/agents")).Should().ContainSingle();
        (await WithDbAsync(db => db.WakeupRequests.IgnoreQueryFilters().CountAsync(w => w.AgentId == agent.Id, Ct))).Should().Be(1);
        (await RunsAsync()).Should().ContainSingle();
    }
}
