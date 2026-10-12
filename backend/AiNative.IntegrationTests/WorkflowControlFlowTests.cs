using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AiNative.Application.Common;
using AiNative.Contracts.Common;
using AiNative.Contracts.Platform;
using AiNative.Contracts.Work;
using AiNative.Contracts.Workflows;
using AiNative.Domain.Common;
using AiNative.Domain.Workflows;
using AiNative.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiNative.IntegrationTests;

/// <summary>
/// 工作流的分支、遍历、HTTP 请求步骤和 Webhook 触发。
/// </summary>
public sealed class WorkflowControlFlowTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const int RunsPerMinute = 3;

    // HTTP 步骤要调用测试里起的本机服务。
    protected override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>
    {
        ["OutboundNetwork:AllowPrivateNetworkRequests"] = "true",
        ["Workflows:MaxLoopItems"] = "5",
        ["Workflows:MaxEventRunsPerMinute"] = RunsPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    [Fact]
    public async Task Branch_runs_the_first_matching_branch_and_exposes_the_choice()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var workflow = await PostAsync<WorkflowDto>(client, "/api/workflows", Manual("按金额分流",
            new WorkflowStep
            {
                Key = "route",
                Name = "按金额分流",
                Kind = WorkflowStepKind.Branch,
                Branches =
                [
                    new WorkflowBranch { Name = "大单", When = "input.amount >= 10000", Steps = [CreateTask("big", "大单 {{ input.customer }}")] },
                    new WorkflowBranch { Name = "其他", Steps = [CreateTask("normal", "普通 {{ input.customer }}")] },
                ],
            },
            CreateTask("after", "走了{{ steps.route.output.branch }}分支")));

        var big = await RunAsync(client, workflow.Id, """{ "customer": "星火", "amount": 50000 }""");
        big.StepRuns.Select(s => (s.Path, s.StepKey, s.Status)).Should().Equal(
            ("0", "route", WorkflowStepStatus.Succeeded),
            ("0.0.0", "big", WorkflowStepStatus.Succeeded),
            ("1", "after", WorkflowStepStatus.Succeeded));
        big.StepRuns[0].Input!.Value.GetProperty("name").GetString().Should().Be("大单");
        big.StepRuns[0].Output!.Value.GetProperty("index").GetInt32().Should().Be(0);

        var small = await RunAsync(client, workflow.Id, """{ "customer": "小店", "amount": 300 }""");
        small.StepRuns.Select(s => s.StepKey).Should().Equal("route", "normal", "after");
        small.Summary.CurrentStep.Should().Be(2, "分支里的步骤不推进顶层进度");

        (await TaskTitlesAsync(client)).Should().BeEquivalentTo("大单 星火", "走了大单分支", "普通 小店", "走了其他分支");
    }

    [Fact]
    public async Task Branch_without_a_match_completes_without_running_anything()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var workflow = await PostAsync<WorkflowDto>(client, "/api/workflows", Manual("只处理紧急",
            new WorkflowStep
            {
                Key = "route",
                Name = "紧急才处理",
                Kind = WorkflowStepKind.Branch,
                Branches = [new WorkflowBranch { Name = "紧急", When = "input.urgent", Steps = [CreateTask("urgent", "紧急")] }],
            }));

        var run = await RunAsync(client, workflow.Id, """{ "urgent": false }""");

        run.StepRuns.Should().ContainSingle().Which.Output!.Value.GetProperty("index").GetInt32().Should().Be(-1);
        (await TaskTitlesAsync(client)).Should().BeEmpty();
    }

    [Fact]
    public async Task For_each_runs_its_steps_for_every_item_with_item_and_index()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var workflow = await PostAsync<WorkflowDto>(client, "/api/workflows", Manual("逐个建任务",
            new WorkflowStep
            {
                Key = "each",
                Name = "逐个处理",
                Kind = WorkflowStepKind.ForEach,
                Items = "{{ input.customers }}",
                Steps =
                [
                    new WorkflowStep
                    {
                        Key = "vip_check",
                        Name = "VIP 另建任务",
                        Kind = WorkflowStepKind.Branch,
                        Branches = [new WorkflowBranch { Name = "VIP", When = "item.vip", Steps = [CreateTask("vip", "VIP {{ item.name }}")] }],
                    },
                    CreateTask("create", "{{ index }}-{{ item.name }}"),
                ],
            },
            CreateTask("summary", "共 {{ steps.each.output.count }} 位，最后是 {{ steps.create.output.title }}")));

        var run = await RunAsync(client, workflow.Id, """{ "customers": [{ "name": "甲" }, { "name": "乙", "vip": true }, { "name": "丙" }] }""");

        var each = run.StepRuns[0];
        each.Input!.Value.GetProperty("count").GetInt32().Should().Be(3);
        each.Output!.Value.GetProperty("count").GetInt32().Should().Be(3);
        each.Output!.Value.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("title").GetString()).Should().Equal("0-甲", "1-乙", "2-丙");
        run.StepRuns.Select(s => s.Path).Should().Equal("0", "0.0.0", "0.0.1", "0.1.0", "0.1.0.0.0", "0.1.1", "0.2.0", "0.2.1", "1");
        (await TaskTitlesAsync(client)).Should().BeEquivalentTo("0-甲", "VIP 乙", "1-乙", "2-丙", "共 3 位，最后是 2-丙");
    }

    [Fact]
    public async Task Loop_that_waits_inside_an_iteration_resumes_where_it_left_off()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var workflow = await PostAsync<WorkflowDto>(client, "/api/workflows", Manual("间隔提醒",
            new WorkflowStep
            {
                Key = "each",
                Name = "逐个提醒",
                Kind = WorkflowStepKind.ForEach,
                Items = "input.names",
                Steps =
                [
                    new WorkflowStep { Key = "wait", Name = "等一秒", Kind = WorkflowStepKind.Delay, DelaySeconds = 1 },
                    CreateTask("remind", "第 {{ index }} 位：{{ item }}"),
                ],
            }));

        var started = await PostAsync<WorkflowRunSummaryDto>(client, $"/api/workflows/{workflow.Id}/runs", new StartWorkflowRunRequest { Input = Json("""{ "names": ["张三", "李四"] }""") });
        await WaitForRunAsync(client, started.Id, WorkflowRunStatus.Waiting);
        var run = await WaitForRunAsync(client, started.Id, WorkflowRunStatus.Succeeded);

        run.StepRuns.Select(s => (s.Path, s.Status)).Should().Equal(
            ("0", WorkflowStepStatus.Succeeded),
            ("0.0.0", WorkflowStepStatus.Succeeded),
            ("0.0.1", WorkflowStepStatus.Succeeded),
            ("0.1.0", WorkflowStepStatus.Succeeded),
            ("0.1.1", WorkflowStepStatus.Succeeded));
        (await TaskTitlesAsync(client)).Should().BeEquivalentTo(["第 0 位：张三", "第 1 位：李四"], "已完成的子步骤按记录重放，不会重复执行");
    }

    [Fact]
    public async Task Loop_over_a_non_list_or_too_many_items_fails()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var workflow = await PostAsync<WorkflowDto>(client, "/api/workflows", Manual("遍历",
            new WorkflowStep { Key = "each", Name = "遍历", Kind = WorkflowStepKind.ForEach, Items = "input.items", Steps = [CreateTask("t", "{{ item }}")] }));

        var tooMany = await RunAsync(client, workflow.Id, """{ "items": [1, 2, 3, 4, 5, 6] }""", WorkflowRunStatus.Failed);
        tooMany.Summary.Error.Should().Contain("上限 5 项");
        var notList = await RunAsync(client, workflow.Id, """{ "items": "abc" }""", WorkflowRunStatus.Failed);
        notList.Summary.Error.Should().Contain("不是列表");
        var empty = await RunAsync(client, workflow.Id, """{ }""");
        empty.StepRuns.Should().ContainSingle().Which.Output!.Value.GetProperty("count").GetInt32().Should().Be(0);
        (await TaskTitlesAsync(client)).Should().BeEmpty();
    }

    [Fact]
    public async Task Failure_inside_a_branch_fails_the_run_unless_the_step_may_continue()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var missing = Guid.NewGuid();
        var workflow = await PostAsync<WorkflowDto>(client, "/api/workflows", Manual("分支里出错",
            new WorkflowStep
            {
                Key = "route",
                Name = "分支",
                Kind = WorkflowStepKind.Branch,
                Branches =
                [
                    new WorkflowBranch
                    {
                        Name = "全部",
                        Steps =
                        [
                            new WorkflowStep { Key = "tolerated", Name = "可以失败", Kind = WorkflowStepKind.Action, ActionKey = "tasks.comment", ContinueOnError = true, Input = Json($$"""{ "taskId": "{{missing}}", "body": "x" }""") },
                            new WorkflowStep { Key = "fatal", Name = "不能失败", Kind = WorkflowStepKind.Action, ActionKey = "tasks.comment", When = "input.strict", Input = Json($$"""{ "taskId": "{{missing}}", "body": "x" }""") },
                        ],
                    },
                ],
            },
            CreateTask("after", "继续执行")));

        var lenient = await RunAsync(client, workflow.Id, """{ "strict": false }""");
        lenient.StepRuns.Select(s => s.Status).Should().Equal(WorkflowStepStatus.Succeeded, WorkflowStepStatus.Failed, WorkflowStepStatus.Skipped, WorkflowStepStatus.Succeeded);

        var strict = await RunAsync(client, workflow.Id, """{ "strict": true }""", WorkflowRunStatus.Failed);
        strict.Summary.Error.Should().StartWith("步骤「不能失败」失败");
        strict.StepRuns.Select(s => (s.StepKey, s.Status)).Should().Equal(
            ("route", WorkflowStepStatus.Failed),
            ("tolerated", WorkflowStepStatus.Failed),
            ("fatal", WorkflowStepStatus.Failed));
        (await TaskTitlesAsync(client)).Should().Equal("继续执行");
    }

    [Fact]
    public async Task Http_step_calls_external_services_and_exposes_the_response()
    {
        await using var server = await EchoServer.StartAsync();
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var workflow = await PostAsync<WorkflowDto>(client, "/api/workflows", Manual("通知外部系统",
            new WorkflowStep
            {
                Key = "call",
                Name = "通知",
                Kind = WorkflowStepKind.Http,
                Method = "post",
                Url = "{{ input.base }}/echo?from=workflow",
                Headers = new() { ["Authorization"] = "Bearer {{ input.secret }}", ["X-Source"] = "ainative" },
                Body = Json("""{ "name": "{{ input.name }}", "tags": ["a", "{{ input.name }}"] }"""),
            },
            CreateTask("record", "对方收到 {{ steps.call.output.body.json.name }}，标记 {{ steps.call.output.body.source }}"),
            new WorkflowStep { Key = "broken", Name = "坏接口", Kind = WorkflowStepKind.Http, Url = "{{ input.base }}/broken", ContinueOnError = true },
            new WorkflowStep { Key = "text", Name = "纯文本", Kind = WorkflowStepKind.Http, Url = "{{ input.base }}/text" }));

        var run = await RunAsync(client, workflow.Id, $$"""{ "base": "{{server.BaseUrl}}", "secret": "s3cr3t", "name": "星火" }""");

        var call = run.StepRuns[0];
        call.Input!.Value.GetProperty("method").GetString().Should().Be("POST");
        call.Input!.Value.GetProperty("url").GetString().Should().Be($"{server.BaseUrl}/echo?from=workflow");
        call.Input!.Value.GetProperty("headers").GetProperty("Authorization").GetString().Should().Be("***", "敏感请求头不写进运行记录");
        call.Output!.Value.GetProperty("status").GetInt32().Should().Be(200);
        call.Output!.Value.GetProperty("ok").GetBoolean().Should().BeTrue();
        var received = server.Requests.Should().ContainSingle(r => r.Path == "/echo").Subject;
        received.Authorization.Should().Be("Bearer s3cr3t");
        received.ContentType.Should().StartWith("application/json");
        JsonDocument.Parse(received.Body).RootElement.GetProperty("tags")[1].GetString().Should().Be("星火");

        var broken = run.StepRuns[2];
        broken.Status.Should().Be(WorkflowStepStatus.Failed);
        broken.Error.Should().Be("服务器返回 HTTP 500。");
        broken.Output!.Value.GetProperty("status").GetInt32().Should().Be(500);
        run.StepRuns[3].Output!.Value.GetProperty("body").GetString().Should().Be("你好");
        (await TaskTitlesAsync(client)).Should().Equal("对方收到 星火，标记 ainative");
    }

    [Fact]
    public async Task Webhook_starts_runs_from_external_requests()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var request = new SaveWorkflowRequest
        {
            Name = "新工单",
            IsEnabled = true,
            Trigger = new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Webhook, Filter = "trigger.body.action == 'opened'" },
            Steps = [CreateTask("create", "{{ trigger.body.title }}（来自 {{ trigger.query.source }}，{{ trigger.headers['x-event'] }}）")],
        };
        var workflow = await PostAsync<WorkflowDto>(client, "/api/workflows", request);
        var token = workflow.WebhookToken;
        token.Should().NotBeNullOrEmpty();
        using var anonymous = Client();

        var accepted = await HookAsync(anonymous, token!, """{ "action": "opened", "title": "打印机坏了" }""", "?source=表单");
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var result = (await accepted.Content.ReadFromJsonAsync<WebhookAcceptedDto>(PlatformJson.Options, Ct))!;
        result.Started.Should().BeTrue();
        var run = await WaitForRunAsync(client, result.RunId!.Value, WorkflowRunStatus.Succeeded);
        run.Summary.TriggerKind.Should().Be(WorkflowTriggerKind.Webhook);
        run.Summary.TriggeredBy.Kind.Should().Be(ActorKind.System);
        run.Trigger.GetProperty("headers").TryGetProperty("authorization", out _).Should().BeFalse("凭证类请求头不进入运行记录");
        (await TaskTitlesAsync(client)).Should().Equal("打印机坏了（来自 表单，issue）");

        // 过滤条件不成立：接收但不运行。
        var filtered = await HookAsync(anonymous, token!, """{ "action": "closed" }""");
        filtered.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await filtered.Content.ReadFromJsonAsync<WebhookAcceptedDto>(PlatformJson.Options, Ct))!.Started.Should().BeFalse();

        (await HookAsync(anonymous, "not-a-token", "{}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var invalid = await anonymous.PostAsync(new Uri($"/api/hooks/workflows/{token}", UriKind.Relative), new StringContent("{ broken", Encoding.UTF8, "application/json"), Ct);
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 频率上限：每分钟最多 RunsPerMinute 次（上面已经运行了 1 次）。
        for (var i = 1; i < RunsPerMinute; i++)
        {
            (await HookAsync(anonymous, token!, """{ "action": "opened", "title": "x" }""")).StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        var limited = await HookAsync(anonymous, token!, """{ "action": "opened", "title": "x" }""");
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await CodeAsync(limited)).Should().Be("workflow.rate_limited");

        // 换地址后旧地址失效；停用后不再接收。
        var regenerated = await PostAsync<WorkflowDto>(client, $"/api/workflows/{workflow.Id}/webhook-token", new { });
        regenerated.WebhookToken.Should().NotBe(token);
        (await HookAsync(anonymous, token!, """{ "action": "opened" }""")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await PutAsync<WorkflowDto>(client, $"/api/workflows/{workflow.Id}/enabled", new SetWorkflowEnabledRequest { Enabled = false });
        (await HookAsync(anonymous, regenerated.WebhookToken!, """{ "action": "opened" }""")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 保存时沿用地址；改成手动触发后地址作废。
        var saved = await PutAsync<WorkflowDto>(client, $"/api/workflows/{workflow.Id}", request with { Name = "新工单（改）" });
        saved.WebhookToken.Should().Be(regenerated.WebhookToken);
        var manual = await PutAsync<WorkflowDto>(client, $"/api/workflows/{workflow.Id}", request with { Trigger = new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Manual } });
        manual.WebhookToken.Should().BeNull();
        var notWebhook = await client.PostAsJsonAsync($"/api/workflows/{workflow.Id}/webhook-token", new { }, PlatformJson.Options, Ct);
        notWebhook.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Webhook_address_is_only_shown_to_members_who_can_manage_workflows()
    {
        var owner = await RegisterAsync();
        using var ownerClient = Client(owner.AccessToken);
        var roles = await GetAsync<List<RoleDto>>(ownerClient, "/api/roles");
        using var viewerClient = Client(await AddMemberAsync(ownerClient, "viewer@example.com", roles.Single(r => r.Key == "viewer").Id));
        var workflow = await PostAsync<WorkflowDto>(ownerClient, "/api/workflows", new SaveWorkflowRequest
        {
            Name = "收单",
            IsEnabled = true,
            Trigger = new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Webhook },
            Steps = [CreateTask("create", "收到")],
        });

        (await GetAsync<WorkflowDto>(viewerClient, $"/api/workflows/{workflow.Id}")).WebhookToken.Should().BeNull();
        (await GetAsync<List<WorkflowDto>>(viewerClient, "/api/workflows")).Single().WebhookToken.Should().BeNull();
        (await GetAsync<WorkflowDto>(ownerClient, $"/api/workflows/{workflow.Id}")).WebhookToken.Should().Be(workflow.WebhookToken);
        (await viewerClient.PostAsJsonAsync($"/api/workflows/{workflow.Id}/webhook-token", new { }, PlatformJson.Options, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 运行落在工作流所在的空间，别的空间看不到。
        using var anonymous = Client();
        var accepted = await (await HookAsync(anonymous, workflow.WebhookToken!, "{}")).Content.ReadFromJsonAsync<WebhookAcceptedDto>(PlatformJson.Options, Ct);
        await WaitForRunAsync(ownerClient, accepted!.RunId!.Value, WorkflowRunStatus.Succeeded);
        var other = await RegisterAsync("other@example.com", "别的空间");
        using var otherClient = Client(other.AccessToken);
        (await GetAsync<PagedResult<WorkflowRunSummaryDto>>(otherClient, "/api/workflows/runs")).Total.Should().Be(0);
        (await otherClient.GetAsync(new Uri($"/api/workflows/runs/{accepted.RunId}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Advanced_steps_are_validated_with_field_errors()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        WorkflowStep Nest(string key, int depth) => depth == 0
            ? CreateTask($"{key}_leaf", "x")
            : new WorkflowStep { Key = $"{key}_{depth}", Name = "嵌套", Kind = WorkflowStepKind.ForEach, Items = "input.items", Steps = [Nest(key, depth - 1)] };

        var response = await client.PostAsJsonAsync("/api/workflows", new SaveWorkflowRequest
        {
            Name = "坏的",
            Trigger = new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Webhook, Filter = "trigger.body ==" },
            Steps =
            [
                new WorkflowStep
                {
                    Key = "route",
                    Name = "分支",
                    Kind = WorkflowStepKind.Branch,
                    Branches =
                    [
                        new WorkflowBranch { Name = "其他", Steps = [CreateTask("dup", "x")] },
                        new WorkflowBranch { Name = "大单", When = "input.amount >", Steps = [CreateTask("dup", "y")] },
                    ],
                },
                new WorkflowStep { Key = "each", Name = "遍历", Kind = WorkflowStepKind.ForEach },
                new WorkflowStep { Key = "call", Name = "请求", Kind = WorkflowStepKind.Http, Method = "TRACE", Url = "ftp://example.com" },
                new WorkflowStep { Key = "get", Name = "带体的 GET", Kind = WorkflowStepKind.Http, Url = "https://example.com", Body = Json("""{ "a": 1 }"""), TimeoutSeconds = 500 },
                Nest("deep", 4),
            ],
        }, PlatformJson.Options, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");
        errors.EnumerateObject().Select(p => p.Name).Should().Contain(
        [
            "trigger.filter",
            "steps[0].branches[0].when",
            "steps[0].branches[1].when",
            "steps[0].branches[1].steps[0].key",
            "steps[1].items",
            "steps[1].steps",
            "steps[2].method",
            "steps[2].url",
            "steps[3].body",
            "steps[3].timeoutSeconds",
            "steps[4].steps[0].steps[0].steps[0].kind",
        ]);
    }

    private static WorkflowStep CreateTask(string key, string title) => new()
    {
        Key = key,
        Name = $"建任务 {key}",
        Kind = WorkflowStepKind.Action,
        ActionKey = "tasks.create",
        Input = JsonDocument.Parse(JsonSerializer.Serialize(new { title })).RootElement.Clone(),
    };

    private static SaveWorkflowRequest Manual(string name, params WorkflowStep[] steps) => new()
    {
        Name = name,
        Trigger = new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Manual },
        Steps = steps,
    };

    private static async Task<WorkflowRunDto> RunAsync(HttpClient client, Guid workflowId, string input, WorkflowRunStatus expected = WorkflowRunStatus.Succeeded)
    {
        var started = await PostAsync<WorkflowRunSummaryDto>(client, $"/api/workflows/{workflowId}/runs", new StartWorkflowRunRequest { Input = Json(input) });
        return await WaitForRunAsync(client, started.Id, expected);
    }

    private static async Task<WorkflowRunDto> WaitForRunAsync(HttpClient client, Guid runId, WorkflowRunStatus status)
    {
        var run = await EventuallyAsync(
            () => GetAsync<WorkflowRunDto>(client, $"/api/workflows/runs/{runId}"),
            r => r.Summary.Status == status || r.Summary.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed or WorkflowRunStatus.Cancelled);
        run.Summary.Status.Should().Be(status, "运行结果：{0}", PlatformJson.Serialize(run));
        return run;
    }

    private static async Task<List<string>> TaskTitlesAsync(HttpClient client)
        => [.. (await GetAsync<PagedResult<WorkItemDto>>(client, "/api/work-items?pageSize=100")).Items.OrderBy(t => t.Key.Length).ThenBy(t => t.Key, StringComparer.Ordinal).Select(t => t.Title)];

    private static Task<HttpResponseMessage> HookAsync(HttpClient client, string token, string json, string query = "")
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"/api/hooks/workflows/{token}{query}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        message.Headers.Add("X-Event", "issue");
        message.Headers.Add("Authorization", "Bearer should-not-be-recorded");
        return client.SendAsync(message, Ct);
    }

    /// <summary>
    /// 本机上的一个小 HTTP 服务，记录收到的请求。
    /// </summary>
    private sealed class EchoServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private EchoServer(WebApplication app, string baseUrl)
        {
            _app = app;
            BaseUrl = baseUrl;
        }

        public string BaseUrl { get; }

        public List<ReceivedRequest> Requests { get; } = [];

        public static async Task<EchoServer> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            EchoServer? server = null;
            app.Use(async (context, next) =>
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                lock (server!.Requests)
                {
                    server.Requests.Add(new ReceivedRequest(context.Request.Path, context.Request.Headers.Authorization.ToString(), context.Request.ContentType, body));
                }

                context.Items["body"] = body;
                await next(context);
            });
            app.MapPost("/echo", (HttpContext context) => Results.Json(new
            {
                json = JsonDocument.Parse((string)context.Items["body"]!).RootElement,
                source = context.Request.Headers["X-Source"].ToString(),
            }));
            app.MapGet("/broken", () => Results.Text("出错了", statusCode: 500));
            app.MapGet("/text", () => Results.Text("你好", "text/plain; charset=utf-8"));
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            server = new EchoServer(app, address.TrimEnd('/'));
            return server;
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }

    private sealed record ReceivedRequest(string Path, string Authorization, string? ContentType, string Body);
}
