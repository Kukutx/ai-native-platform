using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiNative.Application.Common;
using AiNative.Contracts.Auth;
using AiNative.Contracts.Common;
using AiNative.Contracts.Governance;
using AiNative.Contracts.Platform;
using AiNative.Contracts.Work;
using AiNative.Contracts.Workflows;
using AiNative.Domain.Common;
using AiNative.Domain.Governance;
using AiNative.Domain.Work;
using AiNative.Domain.Workflows;
using AiNative.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace AiNative.IntegrationTests;

public sealed class WorkflowTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Manual_run_executes_steps_with_templates_conditions_and_model_output()
    {
        Factory.Script.Handler = (messages, _) => Task.FromResult(messages.Any(m => m.Role == ChatRole.User && (m.Text ?? string.Empty).Contains("请判断紧急程度", StringComparison.Ordinal))
            ? ScriptedModel.Reply("```json\n{\"level\": \"high\"}\n```")
            : ScriptedModel.Reply("好的。"));
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var workflow = await PostAsync<WorkflowDto>(client, "/api/workflows", Manual("客户跟进",
            new WorkflowStep { Key = "classify", Name = "判断紧急程度", Kind = WorkflowStepKind.Llm, Prompt = "请判断紧急程度：{{ input.customer }} 的需求" },
            new WorkflowStep
            {
                Key = "create",
                Name = "建跟进任务",
                Kind = WorkflowStepKind.Action,
                ActionKey = "tasks.create",
                When = "steps.classify.output.json.level == 'high'",
                Input = Json("""{ "title": "跟进 {{ input.customer }}", "description": "级别：{{ steps.classify.output.json.level }}", "priority": "high" }"""),
            },
            new WorkflowStep { Key = "big_deal", Name = "大单另建任务", Kind = WorkflowStepKind.Action, ActionKey = "tasks.create", When = "input.amount > 100000", Input = Json("""{ "title": "大单" }""") },
            new WorkflowStep
            {
                Key = "note",
                Name = "留言",
                Kind = WorkflowStepKind.Action,
                ActionKey = "tasks.comment",
                Input = Json("""{ "taskId": "{{ steps.create.output.id }}", "body": "自动创建，客户 {{ input.customer }}，金额 {{ input.amount }}" }"""),
            }));

        workflow.Owner.Kind.Should().Be(ActorKind.User);
        workflow.Version.Should().Be(1);
        var started = await PostAsync<WorkflowRunSummaryDto>(client, $"/api/workflows/{workflow.Id}/runs", new StartWorkflowRunRequest { Input = Json("""{ "customer": "星火科技", "amount": 5000 }""") });
        var run = await WaitForRunAsync(client, started.Id, WorkflowRunStatus.Succeeded);

        run.Summary.TriggerKind.Should().Be(WorkflowTriggerKind.Manual);
        run.Summary.TriggeredBy.Kind.Should().Be(ActorKind.User);
        run.StepRuns.Select(s => s.Status).Should().Equal(WorkflowStepStatus.Succeeded, WorkflowStepStatus.Succeeded, WorkflowStepStatus.Skipped, WorkflowStepStatus.Succeeded);
        run.StepRuns[0].Output!.Value.GetProperty("json").GetProperty("level").GetString().Should().Be("high");
        run.StepRuns[1].Input!.Value.GetProperty("title").GetString().Should().Be("跟进 星火科技");

        var tasks = await GetAsync<PagedResult<WorkItemDto>>(client, "/api/work-items");
        var task = tasks.Items.Should().ContainSingle().Subject;
        task.Title.Should().Be("跟进 星火科技");
        task.Description.Should().Be("级别：high");
        task.Priority.Should().Be(WorkItemPriority.High);
        var detail = await GetAsync<WorkItemDetailDto>(client, $"/api/work-items/{task.Id}");
        detail.Comments.Should().ContainSingle(c => c.Body == "自动创建，客户 星火科技，金额 5000");

        // 模型调用计入所有者（成员）的用量。
        var costs = await WithDbAsync(db => db.CostEvents.IgnoreQueryFilters().AsNoTracking().ToListAsync(Ct));
        costs.Should().ContainSingle(c => c.Actor.Kind == ActorKind.User && c.Actor.Id == auth.Session.User.Id);
    }

    [Fact]
    public async Task Event_trigger_runs_only_for_matching_events()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        await PostAsync<WorkflowDto>(client, "/api/workflows", new SaveWorkflowRequest
        {
            Name = "完成通知",
            Trigger = new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Event, EventType = "tasks.status_changed", Filter = "trigger.to == 'done'" },
            Steps =
            [
                new WorkflowStep
                {
                    Key = "notify",
                    Name = "留言",
                    Kind = WorkflowStepKind.Action,
                    ActionKey = "tasks.comment",
                    Input = Json("""{ "taskId": "{{ trigger.task.id }}", "body": "{{ trigger.actor.name }} 完成了 {{ trigger.task.key }}（{{ trigger.from }} → {{ trigger.to }}）" }"""),
                },
            ],
        });
        var task = await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "整理合同" });

        await PutAsync<WorkItemDto>(client, $"/api/work-items/{task.Id}", Update(task, WorkItemStatus.InProgress));
        await PutAsync<WorkItemDto>(client, $"/api/work-items/{task.Id}", Update(task, WorkItemStatus.Done));

        var runs = await EventuallyAsync(
            () => GetAsync<PagedResult<WorkflowRunSummaryDto>>(client, "/api/workflows/runs"),
            r => r.Items.Count == 1 && r.Items[0].Status == WorkflowRunStatus.Succeeded);
        runs.Items[0].TriggerKind.Should().Be(WorkflowTriggerKind.Event);
        runs.Items[0].EventType.Should().Be("tasks.status_changed");
        var detail = await GetAsync<WorkItemDetailDto>(client, $"/api/work-items/{task.Id}");
        detail.Comments.Should().ContainSingle(c => c.Body == "owner 完成了 T-1（in_progress → done）");

        await Task.Delay(500, Ct);
        (await GetAsync<PagedResult<WorkflowRunSummaryDto>>(client, "/api/workflows/runs")).Total.Should().Be(1);
    }

    [Fact]
    public async Task Agent_task_step_waits_for_the_agent_and_hands_its_conclusion_to_the_next_step()
    {
        Factory.Script.Handler = (messages, _) => Task.FromResult(
            ScriptedModel.FocusTaskId(messages) is { } taskId && !ScriptedModel.HasToolResult(messages)
                ? ScriptedModel.CallTool("tasks_update_status", new Dictionary<string, object?>
                {
                    ["taskId"] = taskId,
                    ["status"] = "done",
                    ["comment"] = "建议采购 A 方案",
                })
                : ScriptedModel.Reply("已完成。"));
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var agent = await StarterAgentAsync(client);
        var workflow = await PostAsync<WorkflowDto>(client, "/api/workflows", Manual("调研后执行",
            new WorkflowStep
            {
                Key = "research",
                Name = "调研",
                Kind = WorkflowStepKind.AgentTask,
                AgentId = agent.Id,
                Title = "调研 {{ input.topic }}",
                Instructions = "比较市面上的方案。",
                WaitForCompletion = true,
                TimeoutMinutes = 60,
            },
            new WorkflowStep { Key = "execute", Name = "执行", Kind = WorkflowStepKind.Action, ActionKey = "tasks.create", Input = Json("""{ "title": "执行：{{ steps.research.output.result }}" }""") }));

        var started = await PostAsync<WorkflowRunSummaryDto>(client, $"/api/workflows/{workflow.Id}/runs", new StartWorkflowRunRequest { Input = Json("""{ "topic": "CRM 系统" }""") });
        var run = await WaitForRunAsync(client, started.Id, WorkflowRunStatus.Succeeded);

        var research = run.StepRuns[0].Output!.Value;
        research.GetProperty("status").GetString().Should().Be("done");
        research.GetProperty("result").GetString().Should().Be("建议采购 A 方案");
        var tasks = await GetAsync<PagedResult<WorkItemDto>>(client, "/api/work-items?pageSize=10");
        tasks.Items.Select(t => t.Title).Should().BeEquivalentTo("调研 CRM 系统", "执行：建议采购 A 方案");
        tasks.Items.Single(t => t.Title == "调研 CRM 系统").AssigneeAgentId.Should().Be(agent.Id);
    }

    [Fact]
    public async Task Agent_owned_workflow_waits_for_approval_and_resumes_after_the_decision()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var agent = await StarterAgentAsync(client);
        var workflowId = await InsertAgentOwnedWorkflowAsync(auth.Session.Tenant.Id, agent.Id,
            new WorkflowStep { Key = "hire", Name = "招聘", Kind = WorkflowStepKind.Action, ActionKey = "agents.hire", Input = Json("""{ "name": "小研", "title": "研究员", "instructions": "负责市场调研。" }""") });

        var first = await PostAsync<WorkflowRunSummaryDto>(client, $"/api/workflows/{workflowId}/runs", new StartWorkflowRunRequest());
        var waiting = await WaitForRunAsync(client, first.Id, WorkflowRunStatus.Waiting);
        var approval = (await GetAsync<List<ApprovalDto>>(client, "/api/approvals?status=pending")).Should().ContainSingle().Subject;
        approval.WorkflowRunId.Should().Be(first.Id);
        approval.RequestedBy.Id.Should().Be(agent.Id);
        approval.Rationale.Should().Contain("招聘");
        waiting.WaitingApprovalId.Should().Be(approval.Id);

        await PostAsync<ApprovalDto>(client, $"/api/approvals/{approval.Id}/decision", new DecideApprovalRequest { Approve = true });
        var done = await WaitForRunAsync(client, first.Id, WorkflowRunStatus.Succeeded);
        done.StepRuns[0].Output!.Value.GetProperty("name").GetString().Should().Be("小研");
        (await GetAsync<List<AiNative.Contracts.Agents.AgentDto>>(client, "/api/agents")).Should().HaveCount(2);

        var second = await PostAsync<WorkflowRunSummaryDto>(client, $"/api/workflows/{workflowId}/runs", new StartWorkflowRunRequest());
        await WaitForRunAsync(client, second.Id, WorkflowRunStatus.Waiting);
        var next = (await GetAsync<List<ApprovalDto>>(client, "/api/approvals?status=pending")).Single();
        await PostAsync<ApprovalDto>(client, $"/api/approvals/{next.Id}/decision", new DecideApprovalRequest { Approve = false, Note = "人手够了" });
        var rejected = await WaitForRunAsync(client, second.Id, WorkflowRunStatus.Failed);
        rejected.Summary.Error.Should().Contain("审批被拒绝：人手够了");

        // 工作流发起的审批由工作流接着处理，不唤醒智能体。
        (await RunsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelling_a_waiting_run_rejects_its_pending_approval()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var agent = await StarterAgentAsync(client);
        var workflowId = await InsertAgentOwnedWorkflowAsync(auth.Session.Tenant.Id, agent.Id,
            new WorkflowStep { Key = "hire", Name = "招聘", Kind = WorkflowStepKind.Action, ActionKey = "agents.hire", Input = Json("""{ "name": "小研", "title": "研究员", "instructions": "负责市场调研。" }""") });
        var started = await PostAsync<WorkflowRunSummaryDto>(client, $"/api/workflows/{workflowId}/runs", new StartWorkflowRunRequest());
        var waiting = await WaitForRunAsync(client, started.Id, WorkflowRunStatus.Waiting);

        var cancelled = await PostAsync<WorkflowRunSummaryDto>(client, $"/api/workflows/runs/{started.Id}/cancel", new { });

        cancelled.Status.Should().Be(WorkflowRunStatus.Cancelled);
        cancelled.Error.Should().Contain("取消");
        var approval = (await GetAsync<List<ApprovalDto>>(client, "/api/approvals")).Single(a => a.Id == waiting.WaitingApprovalId);
        approval.Status.Should().Be(ApprovalStatus.Rejected);
        var late = await client.PostAsJsonAsync($"/api/approvals/{approval.Id}/decision", new DecideApprovalRequest { Approve = true }, PlatformJson.Options, Ct);
        late.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var again = await client.PostAsync(new Uri($"/api/workflows/runs/{started.Id}/cancel", UriKind.Relative), null, Ct);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await GetAsync<List<AiNative.Contracts.Agents.AgentDto>>(client, "/api/agents")).Should().HaveCount(1);
    }

    [Fact]
    public async Task Workflow_does_not_retrigger_itself()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        await PostAsync<WorkflowDto>(client, "/api/workflows", new SaveWorkflowRequest
        {
            Name = "建任务时补一个检查项",
            Trigger = new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Event, EventType = "tasks.created" },
            Steps = [new WorkflowStep { Key = "check", Name = "补检查项", Kind = WorkflowStepKind.Action, ActionKey = "tasks.create", Input = Json("""{ "title": "检查：{{ trigger.task.title }}" }""") }],
        });

        await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "发布新版本" });

        await EventuallyAsync(
            () => GetAsync<PagedResult<WorkflowRunSummaryDto>>(client, "/api/workflows/runs"),
            r => r.Items.Count == 1 && r.Items[0].Status == WorkflowRunStatus.Succeeded);
        await Task.Delay(800, Ct);
        (await GetAsync<PagedResult<WorkflowRunSummaryDto>>(client, "/api/workflows/runs")).Total.Should().Be(1);
        (await GetAsync<PagedResult<WorkItemDto>>(client, "/api/work-items")).Items.Select(t => t.Title)
            .Should().BeEquivalentTo("发布新版本", "检查：发布新版本");
    }

    [Fact]
    public async Task Scheduled_workflow_fires_when_due_and_moves_to_the_next_occurrence()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var workflow = await PostAsync<WorkflowDto>(client, "/api/workflows", new SaveWorkflowRequest
        {
            Name = "每日站会",
            Trigger = new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Schedule, Cron = "0 9 * * *", TimeZone = "UTC" },
            Steps = [new WorkflowStep { Key = "standup", Name = "建站会任务", Kind = WorkflowStepKind.Action, ActionKey = "tasks.create", Input = Json("""{ "title": "站会 {{ trigger.scheduledAt }}" }""") }],
        });
        workflow.NextRunAt.Should().NotBeNull();
        workflow.NextRunAt!.Value.Hour.Should().Be(9);

        var saved = await GetAsync<WorkflowDto>(client, $"/api/workflows/{workflow.Id}");
        var due = DateTimeOffset.UtcNow.AddMinutes(-1);
        await WithDbAsync(db => db.Workflows.IgnoreQueryFilters().Where(w => w.Id == workflow.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.NextRunAt, due), Ct));

        var runs = await EventuallyAsync(
            () => GetAsync<PagedResult<WorkflowRunSummaryDto>>(client, "/api/workflows/runs"),
            r => r.Items.Count == 1 && r.Items[0].Status == WorkflowRunStatus.Succeeded);
        runs.Items[0].TriggerKind.Should().Be(WorkflowTriggerKind.Schedule);
        runs.Items[0].TriggeredBy.Kind.Should().Be(ActorKind.System);
        var reloaded = await GetAsync<WorkflowDto>(client, $"/api/workflows/{workflow.Id}");
        reloaded.NextRunAt.Should().BeAfter(DateTimeOffset.UtcNow);
        reloaded.UpdatedAt.Should().Be(saved.UpdatedAt, "推进下一次时间不算编辑，不能让正在编辑的人遇到冲突");
        (await GetAsync<PagedResult<WorkItemDto>>(client, "/api/work-items")).Items.Should().ContainSingle(t => t.Title.StartsWith("站会 ", StringComparison.Ordinal));

        await PutAsync<WorkflowDto>(client, $"/api/workflows/{workflow.Id}/enabled", new SetWorkflowEnabledRequest { Enabled = false });
        (await GetAsync<WorkflowDto>(client, $"/api/workflows/{workflow.Id}")).NextRunAt.Should().BeNull();
    }

    [Fact]
    public async Task Delay_step_parks_the_run_until_it_is_due()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var workflow = await PostAsync<WorkflowDto>(client, "/api/workflows", Manual("稍后提醒",
            new WorkflowStep { Key = "wait", Name = "等一会", Kind = WorkflowStepKind.Delay, DelaySeconds = 2 },
            new WorkflowStep { Key = "remind", Name = "提醒", Kind = WorkflowStepKind.Action, ActionKey = "tasks.create", Input = Json("""{ "title": "提醒：{{ steps.wait.output.waitedSeconds }} 秒到了" }""") }));

        var started = await PostAsync<WorkflowRunSummaryDto>(client, $"/api/workflows/{workflow.Id}/runs", new StartWorkflowRunRequest());
        var waiting = await WaitForRunAsync(client, started.Id, WorkflowRunStatus.Waiting);
        waiting.ResumeAt.Should().BeAfter(DateTimeOffset.UtcNow);
        (await GetAsync<PagedResult<WorkItemDto>>(client, "/api/work-items")).Items.Should().BeEmpty();

        await WaitForRunAsync(client, started.Id, WorkflowRunStatus.Succeeded);
        (await GetAsync<PagedResult<WorkItemDto>>(client, "/api/work-items")).Items.Should().ContainSingle(t => t.Title == "提醒：2 秒到了");
    }

    [Fact]
    public async Task Saving_tracks_versions_owner_and_rejects_stale_edits()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var request = Manual("草稿", new WorkflowStep { Key = "a", Name = "建任务", Kind = WorkflowStepKind.Action, ActionKey = "tasks.create", Input = Json("""{ "title": "x" }""") });
        var created = await PostAsync<WorkflowDto>(client, "/api/workflows", request);

        var updated = await PutAsync<WorkflowDto>(client, $"/api/workflows/{created.Id}", request with { Name = "正式版", ExpectedUpdatedAt = created.UpdatedAt });
        var stale = await client.PutAsJsonAsync($"/api/workflows/{created.Id}", request with { Name = "过期修改", ExpectedUpdatedAt = created.UpdatedAt }, PlatformJson.Options, Ct);

        updated.Name.Should().Be("正式版");
        updated.Version.Should().Be(2);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var run = await PostAsync<WorkflowRunSummaryDto>(client, $"/api/workflows/{created.Id}/runs", new StartWorkflowRunRequest());
        await WaitForRunAsync(client, run.Id, WorkflowRunStatus.Succeeded);
        var deleted = await client.DeleteAsync(new Uri($"/api/workflows/{created.Id}", UriKind.Relative), Ct);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync(new Uri($"/api/workflows/runs/{run.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Invalid_definitions_are_rejected_with_field_errors()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);

        var response = await client.PostAsJsonAsync("/api/workflows", new SaveWorkflowRequest
        {
            Name = "坏的",
            Trigger = new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Schedule, Cron = "61 * * * *", TimeZone = "Mars/Olympus" },
            Steps =
            [
                new WorkflowStep { Key = "a", Name = "一", Kind = WorkflowStepKind.Action, ActionKey = "nope.missing" },
                new WorkflowStep { Key = "a", Name = "二", Kind = WorkflowStepKind.Llm, Prompt = "{{ input.x == }}", When = "(" },
                new WorkflowStep { Key = "c", Name = "三", Kind = WorkflowStepKind.Delay },
            ],
        }, PlatformJson.Options, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");
        errors.EnumerateObject().Select(p => p.Name).Should().Contain(
            ["trigger.cron", "trigger.timeZone", "steps[0].actionKey", "steps[1].key", "steps[1].prompt", "steps[1].when", "steps[2].delaySeconds"]);
    }

    [Fact]
    public async Task Workflows_respect_roles_permissions_and_tenant_boundaries()
    {
        var owner = await RegisterAsync();
        using var ownerClient = Client(owner.AccessToken);
        var roles = await GetAsync<List<RoleDto>>(ownerClient, "/api/roles");
        using var viewerClient = Client(await AddMemberAsync(ownerClient, "viewer@example.com", roles.Single(r => r.Key == "viewer").Id));
        using var memberClient = Client(await AddMemberAsync(ownerClient, "member@example.com", roles.Single(r => r.Key == "member").Id));
        var workflow = await PostAsync<WorkflowDto>(ownerClient, "/api/workflows", Manual("负责人专用",
            new WorkflowStep { Key = "a", Name = "建任务", Kind = WorkflowStepKind.Action, ActionKey = "tasks.create", Input = Json("""{ "title": "x" }""") }));

        (await viewerClient.GetAsync(new Uri("/api/workflows", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewerClient.PostAsJsonAsync("/api/workflows", Manual("不允许"), PlatformJson.Options, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewerClient.PostAsJsonAsync($"/api/workflows/{workflow.Id}/runs", new StartWorkflowRunRequest(), PlatformJson.Options, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 成员可以建工作流，但不能把自己没有权限的动作写进去（工作流以保存者身份运行）。
        var escalate = await memberClient.PostAsJsonAsync("/api/workflows", Manual("越权",
            new WorkflowStep { Key = "hire", Name = "招聘", Kind = WorkflowStepKind.Action, ActionKey = "agents.hire", Input = Json("""{ "name": "x", "title": "y" }""") }), PlatformJson.Options, Ct);
        escalate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await escalate.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").TryGetProperty("steps[0].actionKey", out _).Should().BeTrue();

        // 成员修改负责人建的工作流后，运行身份随之变成成员。
        var edited = await PutAsync<WorkflowDto>(memberClient, $"/api/workflows/{workflow.Id}", Manual("成员改过",
            new WorkflowStep { Key = "a", Name = "建任务", Kind = WorkflowStepKind.Action, ActionKey = "tasks.create", Input = Json("""{ "title": "x" }""") }));
        edited.Owner.Name.Should().Be("member");

        var other = await RegisterAsync("other@example.com", "别的空间");
        using var otherClient = Client(other.AccessToken);
        (await otherClient.GetAsync(new Uri($"/api/workflows/{workflow.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await otherClient.PostAsJsonAsync($"/api/workflows/{workflow.Id}/runs", new StartWorkflowRunRequest(), PlatformJson.Options, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync<List<WorkflowDto>>(otherClient, "/api/workflows")).Should().BeEmpty();
    }

    private static SaveWorkflowRequest Manual(string name, params WorkflowStep[] steps) => new()
    {
        Name = name,
        Trigger = new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Manual },
        Steps = steps,
    };

    private static UpdateWorkItemRequest Update(WorkItemDto task, WorkItemStatus status) => new()
    {
        Title = task.Title,
        Description = task.Description,
        Status = status,
        Priority = task.Priority,
    };

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<WorkflowRunDto> WaitForRunAsync(HttpClient client, Guid runId, WorkflowRunStatus status)
    {
        var run = await EventuallyAsync(
            () => GetAsync<WorkflowRunDto>(client, $"/api/workflows/runs/{runId}"),
            r => r.Summary.Status == status || r.Summary.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed or WorkflowRunStatus.Cancelled);
        run.Summary.Status.Should().Be(status, "运行结果：{0}", PlatformJson.Serialize(run));
        return run;
    }

    private async Task<string> AddMemberAsync(HttpClient ownerClient, string email, Guid roleId)
    {
        await PostAsync<MemberDto>(ownerClient, "/api/members", new AddMemberRequest
        {
            Email = email,
            DisplayName = email.Split('@')[0],
            Password = "Passw0rd!x",
            RoleIds = [roleId],
        });
        var login = await PostAsync<AuthResponse>(Client(), "/api/auth/login", new LoginRequest { Email = email, Password = "Passw0rd!x" });
        return login.AccessToken;
    }

    /// <summary>
    /// 智能体所有的工作流（正常由智能体调用 workflows.create 并经人工批准产生），测试里直接落库。
    /// </summary>
    private Task<Guid> InsertAgentOwnedWorkflowAsync(Guid tenantId, Guid agentId, params WorkflowStep[] steps)
        => WithDbAsync(async db =>
        {
            var workflow = new Workflow
            {
                TenantId = tenantId,
                Name = "智能体的工作流",
                TriggerKind = WorkflowTriggerKind.Manual,
                Owner = Actor.ForAgent(agentId),
                Steps = [.. steps],
            };
            db.Workflows.Add(workflow);
            await db.SaveChangesAsync(Ct);
            return workflow.Id;
        });
}
