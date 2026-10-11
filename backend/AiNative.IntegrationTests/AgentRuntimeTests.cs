using AiNative.Application.Tenancy;
using AiNative.Application.Work;
using AiNative.Contracts.Agents;
using AiNative.Contracts.Governance;
using AiNative.Contracts.Work;
using AiNative.Domain.Agents;
using AiNative.Domain.Common;
using AiNative.Domain.Governance;
using AiNative.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AiNative.IntegrationTests;

public sealed class AgentRuntimeTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Assigned_task_wakes_agent_which_comments_and_finishes()
    {
        Factory.Script.Handler = (messages, _) => Task.FromResult(ScriptedModel.HasToolResult(messages)
            ? ScriptedModel.Reply("已开始处理。")
            : ScriptedModel.CallTool("tasks_comment", new Dictionary<string, object?>
            {
                ["taskId"] = ScriptedModel.FocusTaskId(messages),
                ["body"] = "收到，马上处理。",
            }));
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var agent = await StarterAgentAsync(client);

        var task = await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "写周报", AssigneeAgentId = agent.Id });
        task.CreatedAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-5), "保存前返回的 DTO 也要带上真实的创建时间");

        var runs = await EventuallyAsync(RunsAsync, r => r.Count == 1 && r[0].Status == RunStatus.Succeeded);
        var detail = await GetAsync<WorkItemDetailDto>(client, $"/api/work-items/{task.Id}");
        var run = await GetAsync<RunDetailDto>(client, $"/api/runs/{runs[0].Id}");

        detail.Item.Status.Should().Be(AiNative.Domain.Work.WorkItemStatus.InProgress);
        detail.Comments.Should().ContainSingle(c => c.Author.Kind == ActorKind.Agent && c.Body == "收到，马上处理。");
        run.Run.Summary.Should().Be("已开始处理。");
        run.Run.WorkItemId.Should().Be(task.Id);
        run.Events.Select(e => e.Type).Should().ContainInOrder(RunEventType.ToolCall, RunEventType.ToolResult, RunEventType.AssistantText);
        var commentResult = run.Events.First(e => e.Type == RunEventType.ToolResult).Data.GetProperty("output");
        commentResult.GetProperty("createdAt").GetDateTimeOffset().Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-5));
        (await GetAsync<AgentDto>(client, $"/api/agents/{agent.Id}")).Status.Should().Be(AgentStatus.Idle);

        // 智能体自己的评论不会再次唤醒自己。
        await Task.Delay(500, Ct);
        (await RunsAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task Wakeups_coalesce_while_agent_is_paused_and_run_once_after_resume()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var agent = await StarterAgentAsync(client);
        await PostAsync<AgentDto>(client, $"/api/agents/{agent.Id}/pause", new { });

        await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "任务一", AssigneeAgentId = agent.Id });
        await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "任务二", AssigneeAgentId = agent.Id });
        await PostAsync<WakeupDto>(client, $"/api/agents/{agent.Id}/wake", new WakeAgentRequest { Reason = "看看进度" });

        var queued = await WithDbAsync(db => db.WakeupRequests.IgnoreQueryFilters().AsNoTracking().Where(w => w.AgentId == agent.Id).ToListAsync(Ct));
        queued.Should().ContainSingle();
        queued[0].Status.Should().Be(WakeupStatus.Queued);
        queued[0].Items.Should().HaveCount(3);
        queued[0].CoalescedCount.Should().Be(2);
        (await RunsAsync()).Should().BeEmpty();

        await PostAsync<AgentDto>(client, $"/api/agents/{agent.Id}/resume", new { });

        var runs = await EventuallyAsync(RunsAsync, r => r.Count == 1 && r[0].Status == RunStatus.Succeeded);
        runs[0].WakeupRequestId.Should().Be(queued[0].Id);
    }

    [Fact]
    public async Task Hiring_by_agent_waits_for_approval_then_executes_and_wakes_requester()
    {
        var hireRequested = false;
        Factory.Script.Handler = (messages, _) =>
        {
            if (!hireRequested && !ScriptedModel.HasToolResult(messages))
            {
                hireRequested = true;
                return Task.FromResult(ScriptedModel.CallTool("agents_hire", new Dictionary<string, object?>
                {
                    ["name"] = "小研",
                    ["title"] = "研究员",
                    ["instructions"] = "负责市场调研。",
                }, text: "需要一名研究员协助。"));
            }

            return Task.FromResult(ScriptedModel.Reply("已提交招聘申请。"));
        };
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var lead = await StarterAgentAsync(client);

        await PostAsync<WakeupDto>(client, $"/api/agents/{lead.Id}/wake", new WakeAgentRequest { Reason = "组建团队" });
        await EventuallyAsync(RunsAsync, r => r.Count == 1 && r[0].Status == RunStatus.Succeeded);

        var approval = (await GetAsync<List<ApprovalDto>>(client, "/api/approvals?status=pending")).Should().ContainSingle().Subject;
        approval.ActionKey.Should().Be("agents.hire");
        approval.Rationale.Should().Be("需要一名研究员协助。");
        (await GetAsync<List<AgentDto>>(client, "/api/agents")).Should().HaveCount(1);

        var decided = await PostAsync<ApprovalDto>(client, $"/api/approvals/{approval.Id}/decision", new DecideApprovalRequest { Approve = true, Note = "同意" });

        decided.Status.Should().Be(ApprovalStatus.Approved);
        decided.ExecutionError.Should().BeNull();
        var hired = (await GetAsync<List<AgentDto>>(client, "/api/agents")).Single(a => a.Id != lead.Id);
        hired.Name.Should().Be("小研");
        hired.ReportsToId.Should().Be(lead.Id);

        // 审批结果唤醒申请者继续工作；重复决定被拒绝。
        await EventuallyAsync(RunsAsync, r => r.Count == 2 && r.All(x => x.Status == RunStatus.Succeeded));
        var again = await client.PostAsync(
            new Uri($"/api/approvals/{approval.Id}/decision", UriKind.Relative),
            System.Net.Http.Json.JsonContent.Create(new DecideApprovalRequest { Approve = false }),
            Ct);
        again.StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Exceeding_budget_pauses_agent()
    {
        // 一次请求 1,000,000 输入 token × 10 美元/百万 = 10 美元，超过 1 美元预算。
        Factory.Script.Handler = (messages, _) => Task.FromResult(ScriptedModel.HasToolResult(messages)
            ? ScriptedModel.Reply("继续。")
            : ScriptedModel.CallTool("tasks_list", new Dictionary<string, object?>(), inputTokens: 1_000_000));
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var agent = await StarterAgentAsync(client);
        await PutAsync<AgentDto>(client, $"/api/agents/{agent.Id}", new SaveAgentRequest
        {
            Name = agent.Name,
            Title = agent.Title,
            Instructions = agent.Instructions,
            Permissions = agent.Permissions,
            MonthlyBudgetUsd = 1m,
        });

        await PostAsync<WakeupDto>(client, $"/api/agents/{agent.Id}/wake", new WakeAgentRequest());

        var runs = await EventuallyAsync(RunsAsync, r => r.Count == 1 && r[0].Status == RunStatus.Failed);
        var paused = await GetAsync<AgentDto>(client, $"/api/agents/{agent.Id}");

        runs[0].CostUsd.Should().Be(10m);
        paused.Status.Should().Be(AgentStatus.Paused);
        paused.SpentThisMonthUsd.Should().Be(10m);
        paused.PauseReason.Should().Contain("预算");
    }

    [Fact]
    public async Task Task_checkout_has_exactly_one_winner()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var agent = await StarterAgentAsync(client);
        await PostAsync<AgentDto>(client, $"/api/agents/{agent.Id}/pause", new { });
        var task = await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "抢任务", AssigneeAgentId = agent.Id });
        var tenantId = auth.Session.Tenant.Id;
        var scopes = Factory.Services.GetRequiredService<ITenantScopeFactory>();

        Task<bool> TryCheckout(Guid runId) => scopes.RunAsync(tenantId, Actor.System, [], sp =>
            sp.GetRequiredService<IWorkItemCheckout>().TryCheckoutAsync(task.Id, agent.Id, runId, Ct));

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => TryCheckout(i % 2 == 0 ? first : second)));

        var winners = results.Select((won, i) => (won, run: i % 2 == 0 ? first : second)).Where(x => x.won).Select(x => x.run).Distinct().ToList();
        winners.Should().ContainSingle();

        await scopes.RunAsync(tenantId, Actor.System, [], async sp =>
        {
            await sp.GetRequiredService<IWorkItemCheckout>().ReleaseAsync(winners[0], Ct);
            return true;
        });
        var loser = winners[0] == first ? second : first;
        (await TryCheckout(loser)).Should().BeTrue();
    }
}
