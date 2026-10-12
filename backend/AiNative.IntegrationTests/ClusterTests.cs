using System.Net;
using AiNative.Application.Common;
using AiNative.Contracts.Agents;
using AiNative.Contracts.Auth;
using AiNative.Contracts.Common;
using AiNative.Contracts.Platform;
using AiNative.Contracts.Realtime;
using AiNative.Contracts.Work;
using AiNative.Contracts.Workflows;
using AiNative.Domain.Agents;
using AiNative.Domain.Common;
using AiNative.Domain.Governance;
using AiNative.Domain.Identity;
using AiNative.Domain.Workflows;
using AiNative.Infrastructure.Cluster;
using AiNative.Infrastructure.Workflows;
using AiNative.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiNative.IntegrationTests;

/// <summary>
/// 多实例部署：两个宿主连同一个数据库，验证实时推送、缓存失效的跨实例广播，以及失联实例遗留工作的接手。
/// </summary>
public sealed class ClusterTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private static readonly Dictionary<string, string> ApiOnly = new()
    {
        ["Scheduler:Enabled"] = "false",
        ["Workflows:Enabled"] = "false",
    };

    [Fact]
    public async Task Entity_changes_reach_clients_connected_to_another_instance()
    {
        await using var peer = StartPeer(ApiOnly);
        await WaitForPeersAsync(peer);
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);

        await using var hub = Hub(peer, auth.AccessToken);
        var received = new TaskCompletionSource<RealtimeChangeDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<RealtimeChangeDto>(RealtimeMethods.Change, change =>
        {
            if (change is { Entity: "work_item", Kind: "created" })
            {
                received.TrySetResult(change);
            }
        });
        await hub.StartAsync(Ct);

        var task = await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "跨实例推送" });

        (await received.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Id.Should().Be(task.Id);
    }

    [Fact]
    public async Task Long_run_output_reaches_another_instance_through_the_message_table()
    {
        // 远超 NOTIFY 上限（8000 字节）的输出，正文经 cluster_messages 转交。
        var text = string.Concat(Enumerable.Repeat("跨实例的长输出。", 1500));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Factory.Script.Handler = async (_, _) =>
        {
            await gate.Task;
            return ScriptedModel.Reply(text);
        };
        await using var peer = StartPeer(ApiOnly);
        await WaitForPeersAsync(peer);
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var agent = await StarterAgentAsync(client);

        await using var hub = Hub(peer, auth.AccessToken);
        var received = new TaskCompletionSource<RunEventDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<RunEventDto>(RealtimeMethods.RunEvent, evt =>
        {
            if (evt.Type == RunEventType.AssistantText && evt.Seq > 0)
            {
                received.TrySetResult(evt);
            }
        });
        await hub.StartAsync(Ct);

        await PostAsync<WakeupDto>(client, $"/api/agents/{agent.Id}/wake", new WakeAgentRequest());
        var runs = await EventuallyAsync(RunsAsync, r => r.Count == 1 && r[0].Status == RunStatus.Running);
        await hub.InvokeAsync("SubscribeRun", runs[0].Id, Ct);
        gate.SetResult();

        var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        evt.Data.GetProperty("text").GetString().Should().Be(text);
        (await WithDbAsync(db => db.ClusterMessages.CountAsync(Ct))).Should().BePositive();
    }

    [Fact]
    public async Task Suspending_a_member_takes_effect_on_every_instance_right_away()
    {
        await using var peer = StartPeer(ApiOnly);
        await WaitForPeersAsync(peer);
        var owner = await RegisterAsync();
        using var ownerClient = Client(owner.AccessToken);
        var viewer = (await GetAsync<List<RoleDto>>(ownerClient, "/api/roles")).Single(r => r.Key == "viewer");
        var token = await AddMemberAsync(ownerClient, "member@example.com", viewer.Id);
        var member = (await GetAsync<List<MemberDto>>(ownerClient, "/api/members")).Single(m => m.Email == "member@example.com");
        using var memberOnPeer = ClientOf(peer, token);

        // 先让另一个实例缓存这名成员的权限。
        (await memberOnPeer.GetAsync(new Uri("/api/work-items", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        await PutAsync<MemberDto>(ownerClient, $"/api/members/{member.MembershipId}", new UpdateMemberRequest { RoleIds = member.RoleIds, Status = MembershipStatus.Suspended });

        // 权限缓存 10 秒过期；3 秒内生效说明靠的是广播失效。
        await EventuallyAsync(
            () => memberOnPeer.GetAsync(new Uri("/api/work-items", UriKind.Relative), Ct),
            r => !r.IsSuccessStatusCode,
            TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Agent_run_left_by_a_lost_instance_is_failed_and_its_wakeup_retried()
    {
        Factory.Script.Handler = (_, _) => Task.FromResult(ScriptedModel.Reply("接手完成"));
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var agent = await StarterAgentAsync(client);
        var peerId = await FakeInstanceAsync();
        var (wakeupId, runId) = await WithDbAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var wakeup = new WakeupRequest
            {
                TenantId = auth.Session.Tenant.Id,
                AgentId = agent.Id,
                Status = WakeupStatus.Claimed,
                Items = [new WakeupItem { Source = WakeupSource.OnDemand, Reason = "接着处理", RequestedAt = now }],
                RequestedBy = Actor.ForUser(auth.Session.User.Id),
                ClaimedAt = now,
            };
            var run = new HeartbeatRun
            {
                TenantId = auth.Session.Tenant.Id,
                AgentId = agent.Id,
                WakeupRequestId = wakeup.Id,
                Status = RunStatus.Running,
                StartedAt = now,
                ExecutorId = peerId,
            };
            wakeup.RunId = run.Id;
            db.AddRange(wakeup, run);
            await db.SaveChangesAsync(Ct);
            await db.Agents.IgnoreQueryFilters().Where(a => a.Id == agent.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AgentStatus.Running), Ct);
            return (wakeup.Id, run.Id);
        });

        // 执行实例仍在报到：不动它。
        await WaitForRecoverySweepAsync(auth);
        (await RunAsync(runId)).Status.Should().Be(RunStatus.Running);

        await ExpireInstanceAsync(peerId);
        var failed = await EventuallyAsync(() => RunAsync(runId), r => r.Status == RunStatus.Failed);
        var runs = await EventuallyAsync(RunsAsync, r => r.Any(x => x.Id != runId && x.Status == RunStatus.Succeeded));

        failed.Error.Should().Contain("失联");
        var retried = runs.Single(x => x.Id != runId);
        retried.WakeupRequestId.Should().Be(wakeupId);
        retried.ExecutorId.Should().Be(Node(Factory).InstanceId);
    }

    [Fact]
    public async Task Workflow_run_left_by_a_lost_instance_resumes_here_and_gives_up_after_repeated_interruptions()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var workflow = await PostAsync<WorkflowDto>(client, "/api/workflows", new SaveWorkflowRequest
        {
            Name = "接手",
            Trigger = new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Manual },
            Steps = [new WorkflowStep { Key = "a", Name = "建任务", Kind = WorkflowStepKind.Action, ActionKey = "tasks.create", Input = Json("""{ "title": "接手后完成" }""") }],
        });
        var lost = Guid.NewGuid();
        var resumable = await InsertWorkflowRunAsync(auth, workflow, lost, interruptions: 1);
        var exhausted = await InsertWorkflowRunAsync(auth, workflow, lost, interruptions: WorkflowRunRecovery.MaxInterruptions - 1);

        var resumed = await EventuallyAsync(() => WorkflowRunAsync(resumable), r => r.Status == WorkflowRunStatus.Succeeded);
        var failed = await EventuallyAsync(() => WorkflowRunAsync(exhausted), r => r.Status == WorkflowRunStatus.Failed);

        resumed.ExecutorId.Should().Be(Node(Factory).InstanceId);
        resumed.InterruptionCount.Should().Be(0);
        failed.InterruptionCount.Should().Be(WorkflowRunRecovery.MaxInterruptions);
        failed.Error.Should().Contain("多次意外中断");
        (await GetAsync<PagedResult<WorkItemDto>>(client, "/api/work-items")).Items.Should().ContainSingle(t => t.Title == "接手后完成");
    }

    [Fact]
    public async Task Approval_executed_by_a_lost_instance_is_flagged_without_waiting_for_the_grace_period()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var agent = await StarterAgentAsync(client);
        var peerId = await FakeInstanceAsync();
        var onLivePeer = await InsertApprovalAsync(auth, Actor.ForAgent(agent.Id), peerId);
        var onLostPeer = await InsertApprovalAsync(auth, Actor.ForAgent(agent.Id), Guid.NewGuid());

        var flagged = await EventuallyAsync(() => ApprovalAsync(onLostPeer), a => a.ExecutionError is not null);
        await WaitForRecoverySweepAsync(auth);

        flagged.ExecutionError.Should().Contain("结果未知");
        (await ApprovalAsync(onLivePeer)).ExecutionError.Should().BeNull();
    }

    [Fact]
    public async Task Stopping_an_instance_unregisters_it_and_hands_back_its_workflow_runs()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Factory.Script.Handler = (_, _) =>
        {
            started.TrySetResult();
            return new TaskCompletionSource<ChatResponse>().Task;
        };
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var workflow = await PostAsync<WorkflowDto>(client, "/api/workflows", new SaveWorkflowRequest
        {
            Name = "慢步骤",
            Trigger = new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Manual },
            Steps = [new WorkflowStep { Key = "think", Name = "想一想", Kind = WorkflowStepKind.Llm, Prompt = "慢慢想" }],
        });
        var run = await PostAsync<WorkflowRunSummaryDto>(client, $"/api/workflows/{workflow.Id}/runs", new StartWorkflowRunRequest());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // 停掉本实例的工作流引擎（其他实例都不执行工作流）：执行中的运行放回队列，不算意外中断。
        await Factory.Services.GetServices<IHostedService>().OfType<WorkflowEngine>().Single().StopAsync(Ct);
        var released = await WorkflowRunAsync(run.Id);
        released.Status.Should().Be(WorkflowRunStatus.Queued);
        released.InterruptionCount.Should().Be(0);

        var peer = StartPeer(ApiOnly);
        var peerId = Node(peer).InstanceId;
        (await WithDbAsync(db => db.ClusterInstances.AnyAsync(i => i.Id == peerId, Ct))).Should().BeTrue();
        await peer.DisposeAsync();
        (await WithDbAsync(db => db.ClusterInstances.AnyAsync(i => i.Id == peerId, Ct))).Should().BeFalse();
    }

    [Fact]
    public async Task Instance_that_lost_its_registration_stops_local_runs_and_registers_again()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Factory.Script.Handler = (_, _) =>
        {
            started.TrySetResult();
            return new TaskCompletionSource<ChatResponse>().Task;
        };
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var agent = await StarterAgentAsync(client);
        await PostAsync<WakeupDto>(client, $"/api/agents/{agent.Id}/wake", new WakeAgentRequest());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var node = Node(Factory);
        var lease = node.LeaseToken;

        // 登记被清理（例如长时间失联后被其他实例删掉）：下一次报到发现后先停下手里的运行，再重新登记。
        await WithDbAsync(db => db.ClusterInstances.Where(i => i.Id == node.InstanceId).ExecuteDeleteAsync(Ct));

        await EventuallyAsync(() => Task.FromResult(lease.IsCancellationRequested), cancelled => cancelled, TimeSpan.FromSeconds(5));
        var runs = await EventuallyAsync(RunsAsync, r => r.Single().Status == RunStatus.Cancelled);
        await EventuallyAsync(() => WithDbAsync(db => db.ClusterInstances.AnyAsync(i => i.Id == node.InstanceId, Ct)), registered => registered);
        node.IsRegistered.Should().BeTrue();
        node.LeaseToken.IsCancellationRequested.Should().BeFalse();
        runs.Single().ExecutorId.Should().Be(node.InstanceId);
    }

    private static ClusterNode Node(ApiFactory factory) => factory.Services.GetRequiredService<ClusterNode>();

    private Task WaitForPeersAsync(ApiFactory peer)
        => EventuallyAsync(() => Task.FromResult(Node(Factory).HasPeers && Node(peer).HasPeers), ready => ready, TimeSpan.FromSeconds(10));

    private static HubConnection Hub(ApiFactory factory, string token) => new HubConnectionBuilder()
        .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/realtime"), options =>
        {
            options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            options.Transports = HttpTransportType.LongPolling;
            options.AccessTokenProvider = () => Task.FromResult<string?>(token);
        })
        .AddJsonProtocol(options => PlatformJson.Apply(options.PayloadSerializerOptions))
        .Build();

    /// <summary>
    /// 登记一个刚报到过、实际并不存在的实例，用来模拟工作被另一个实例领取。
    /// </summary>
    private async Task<Guid> FakeInstanceAsync()
    {
        var id = Guid.NewGuid();
        await WithDbAsync(db => db.Database.ExecuteSqlAsync($"INSERT INTO cluster_instances (id, host_name, started_at, last_seen_at) VALUES ({id}, 'peer', now(), now())", Ct));
        return id;
    }

    private Task ExpireInstanceAsync(Guid id)
        => WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE cluster_instances SET last_seen_at = now() - interval '1 hour' WHERE id = {id}", Ct));

    private Task<HeartbeatRun> RunAsync(Guid id)
        => WithDbAsync(db => db.HeartbeatRuns.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == id, Ct));

    private Task<WorkflowRun> WorkflowRunAsync(Guid id)
        => WithDbAsync(db => db.WorkflowRuns.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == id, Ct));

    private Task<Approval> ApprovalAsync(Guid id)
        => WithDbAsync(db => db.Approvals.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == id, Ct));

    private Task<Guid> InsertWorkflowRunAsync(AuthResponse auth, WorkflowDto workflow, Guid executor, int interruptions)
        => WithDbAsync(async db =>
        {
            var run = new WorkflowRun
            {
                TenantId = auth.Session.Tenant.Id,
                WorkflowId = workflow.Id,
                WorkflowVersion = workflow.Version,
                WorkflowName = workflow.Name,
                Steps = [.. workflow.Steps],
                Owner = Actor.ForUser(auth.Session.User.Id),
                TriggeredBy = Actor.ForUser(auth.Session.User.Id),
                TriggerKind = WorkflowTriggerKind.Manual,
                Status = WorkflowRunStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
                ExecutorId = executor,
                InterruptionCount = interruptions,
            };
            db.WorkflowRuns.Add(run);
            await db.SaveChangesAsync(Ct);
            return run.Id;
        });

    /// <summary>
    /// 等恢复扫描在此之后完整跑完一轮，代替固定延时：插入一条记在已失联实例名下、结果没写回的已批准审批，等它被标记。
    /// 每轮恢复先扫运行、再扫审批，标记第一条的那一轮可能在调用之前就扫完了运行，所以连做两次：
    /// 标记第二条的那一轮整个都发生在调用之后，此前插入的运行和审批都至少被它检查过一次。
    /// 申请人是成员，标记后不会唤醒任何智能体。
    /// </summary>
    private async Task WaitForRecoverySweepAsync(AuthResponse auth)
    {
        for (var i = 0; i < 2; i++)
        {
            var probe = await InsertApprovalAsync(auth, Actor.ForUser(auth.Session.User.Id), Guid.NewGuid());
            await EventuallyAsync(() => ApprovalAsync(probe), a => a.ExecutionError is not null);
        }
    }

    private Task<Guid> InsertApprovalAsync(AuthResponse auth, Actor requestedBy, Guid executor)
        => WithDbAsync(async db =>
        {
            var approval = new Approval
            {
                TenantId = auth.Session.Tenant.Id,
                ActionKey = "agents.hire",
                RequestedBy = requestedBy,
                Status = ApprovalStatus.Approved,
                DecidedByUserId = auth.Session.User.Id,
                DecidedAt = DateTimeOffset.UtcNow,
                ExecutorId = executor,
            };
            db.Approvals.Add(approval);
            await db.SaveChangesAsync(Ct);
            return approval.Id;
        });
}
