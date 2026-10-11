using AiNative.Application.Common;
using AiNative.Contracts.Agents;
using AiNative.Contracts.Realtime;
using AiNative.Contracts.Work;
using AiNative.Domain.Agents;
using AiNative.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace AiNative.IntegrationTests;

public sealed class RealtimeTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Hub_pushes_entity_changes_and_live_run_events()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Factory.Script.Handler = async (_, _) =>
        {
            await gate.Task;
            return ScriptedModel.Reply("实时输出");
        };
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var agent = await StarterAgentAsync(client);

        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(Factory.Server.BaseAddress, "/hubs/realtime"), options =>
            {
                options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(auth.AccessToken);
            })
            .AddJsonProtocol(options => PlatformJson.Apply(options.PayloadSerializerOptions))
            .Build();
        var changes = new List<RealtimeChangeDto>();
        var events = new List<RunEventDto>();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<RealtimeChangeDto>(RealtimeMethods.Change, change => { lock (changes) { changes.Add(change); } });
        hub.On<RunEventDto>(RealtimeMethods.RunEvent, evt =>
        {
            lock (events)
            {
                events.Add(evt);
            }

            if (evt.Type == RunEventType.Status && evt.Data.GetProperty("status").GetString() == "succeeded")
            {
                finished.TrySetResult();
            }
        });
        await hub.StartAsync(Ct);

        await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "实时任务", AssigneeAgentId = agent.Id });
        var runs = await EventuallyAsync(RunsAsync, r => r.Count == 1 && r[0].Status == RunStatus.Running);
        await hub.InvokeAsync("SubscribeRun", runs[0].Id, Ct);
        gate.SetResult();

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        lock (changes)
        {
            changes.Should().Contain(c => c.Entity == "work_item" && c.Kind == "created");
        }

        lock (events)
        {
            events.Should().Contain(e => e.Seq == 0 && e.Data.GetProperty("delta").GetString() == "实时输出");
            events.Should().Contain(e => e.Type == RunEventType.AssistantText && e.Seq > 0);
        }
    }

    [Fact]
    public async Task Hub_rejects_runs_from_other_spaces()
    {
        var alice = await RegisterAsync("alice@example.com", "A");
        var bob = await RegisterAsync("bob@example.com", "B");
        using var aliceClient = Client(alice.AccessToken);
        var agent = await StarterAgentAsync(aliceClient);
        await PostAsync<WakeupDto>(aliceClient, $"/api/agents/{agent.Id}/wake", new WakeAgentRequest());
        var run = (await EventuallyAsync(RunsAsync, r => r.Count == 1))[0];

        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(Factory.Server.BaseAddress, "/hubs/realtime"), options =>
            {
                options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(bob.AccessToken);
            })
            .Build();
        await hub.StartAsync(Ct);

        var act = () => hub.InvokeAsync("SubscribeRun", run.Id, Ct);

        await act.Should().ThrowAsync<Microsoft.AspNetCore.SignalR.HubException>();
    }
}
