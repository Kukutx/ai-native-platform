using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AiNative.Application.Common;
using AiNative.Contracts.Actions;
using AiNative.Contracts.Agents;
using AiNative.Contracts.Governance;
using AiNative.Contracts.Integrations;
using AiNative.Contracts.Platform;
using AiNative.Contracts.Workflows;
using AiNative.Domain.Agents;
using AiNative.Domain.Governance;
using AiNative.Domain.Integrations;
using AiNative.Domain.Workflows;
using AiNative.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AiNative.IntegrationTests;

/// <summary>
/// 集成：空间密钥、外部 MCP 服务的工具成为平台动作（界面、智能体、工作流共用）、工作流请求头引用密钥。
/// </summary>
public sealed class IntegrationsTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string Token = "s3cr3t-token-value";

    // 外部服务是测试里起的本机服务。
    protected override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>
    {
        ["OutboundNetwork:AllowPrivateNetworkRequests"] = "true",
    };

    [Fact]
    public async Task Mcp_server_tools_become_actions_and_secrets_reach_only_the_server()
    {
        await using var server = await TestMcpServer.StartAsync();
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);

        var secret = await PostAsync<TenantSecretDto>(client, "/api/integrations/secrets", new CreateTenantSecretRequest { Name = "MCP_TOKEN", Value = Token, Description = "测试服务" });
        var mcp = await PostAsync<McpServerDto>(client, "/api/integrations/mcp-servers", Server(server.Url));

        mcp.LastError.Should().BeNull();
        mcp.ToolsRefreshedAt.Should().NotBeNull();
        // 服务自己声明的只读不算数：管理员确认之前都要审批。
        mcp.Tools.Select(t => (t.Name, t.Key, t.ReadOnly, t.ReadOnlyHint, t.RequiresApproval)).Should().BeEquivalentTo(new[]
        {
            ("echo", "mcp.test.echo", false, true, true),
            ("issues/create", "mcp.test.issues_create", false, false, true),
            ("explode", "mcp.test.explode", false, false, true),
            ("whoami", "mcp.test.whoami", false, false, true),
        });
        server.Authorizations.Should().NotBeEmpty().And.OnlyContain(a => a == $"Bearer {Token}");

        // 值不出现在任何接口和数据库明文里。
        var secretsJson = await client.GetStringAsync(new Uri("/api/integrations/secrets", UriKind.Relative), Ct);
        secretsJson.Should().Contain("MCP_TOKEN").And.NotContain(Token);
        var cipher = await WithDbAsync(db => db.TenantSecrets.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == secret.Id, Ct));
        Encoding.UTF8.GetString(cipher.CipherText).Should().NotContain(Token);

        var actions = await GetAsync<List<ActionDescriptorDto>>(client, "/api/actions");
        var echo = actions.Should().ContainSingle(a => a.Key == "mcp.test.echo").Subject;
        echo.ModuleKey.Should().Be("integrations");
        echo.Title.Should().Be("测试服务 · 回声");
        echo.InputSchema.GetProperty("properties").GetProperty("text").GetProperty("type").GetString().Should().Be("string");

        (await InvokeAsync(client, "mcp.test.echo", new { text = "你好" })).GetProperty("content").GetString().Should().Be("回声：你好");
        var created = await InvokeAsync(client, "mcp.test.issues_create", new { title = "登录失败" });
        created.GetProperty("structured").GetProperty("number").GetInt32().Should().Be(42);
        created.GetProperty("source").GetString().Should().Be("测试服务");

        var failed = await PostAsync<InvokeActionResponse>(client, "/api/actions/mcp.test.explode/invoke", new { });
        failed.Status.Should().Be("failed");
        failed.Error.Should().Be("仓库不存在");

        // 服务把令牌原样回显时，结果里抹掉它，不写进执行记录、不交给模型。
        var whoami = await InvokeAsync(client, "mcp.test.whoami", new { });
        whoami.GetProperty("content").GetString().Should().Be("收到令牌：Bearer ***");
        whoami.GetProperty("structured").GetProperty("authorization").GetString().Should().Be("Bearer ***");
        PlatformJson.Serialize(whoami).Should().NotContain(Token);

        // 确认只读后不再需要审批；关掉的工具从目录里消失，也不能再调用。
        var confirmed = await PutAsync<McpServerDto>(client, $"/api/integrations/mcp-servers/{mcp.Id}/tools", new UpdateMcpToolRequest { Tool = "echo", ReadOnly = true });
        confirmed.Tools.Single(t => t.Name == "echo").Should().Match<McpToolDto>(t => t.ReadOnly && !t.RequiresApproval && t.Enabled);
        var updated = await PutAsync<McpServerDto>(client, $"/api/integrations/mcp-servers/{mcp.Id}/tools", new UpdateMcpToolRequest { Tool = "echo", Enabled = false });
        updated.Tools.Single(t => t.Name == "echo").Should().Match<McpToolDto>(t => t.ReadOnly && !t.Enabled, "只改给出的项");
        (await GetAsync<List<ActionDescriptorDto>>(client, "/api/actions")).Should().NotContain(a => a.Key == "mcp.test.echo");
        (await PostAsync<InvokeActionResponse>(client, "/api/actions/mcp.test.echo/invoke", new { text = "x" })).Status.Should().Be("not_found");
        (await client.PutAsJsonAsync($"/api/integrations/mcp-servers/{mcp.Id}/tools", new UpdateMcpToolRequest { Tool = "不存在", Enabled = false }, PlatformJson.Options, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 刷新工具列表保留管理员的调整和工具标识。
        var refreshed = await PostAsync<McpServerDto>(client, $"/api/integrations/mcp-servers/{mcp.Id}/refresh", new { });
        refreshed.Tools.Single(t => t.Name == "echo").Should().Match<McpToolDto>(t => t.ReadOnly && !t.Enabled && t.Key == "mcp.test.echo");

        // 平台自己的 MCP 服务不转出外部工具。
        var mcpTools = await PlatformToolsAsync(auth.AccessToken);
        mcpTools.Should().NotBeEmpty().And.NotContain(name => name.StartsWith("mcp_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Agent_uses_read_only_tools_directly_and_needs_approval_for_writes()
    {
        await using var server = await TestMcpServer.StartAsync();
        var step = 0;
        Factory.Script.Handler = (messages, options) =>
        {
            step++;
            return Task.FromResult(step switch
            {
                1 => ScriptedModel.CallTool("mcp_test_echo", new Dictionary<string, object?> { ["text"] = "查一下" }),
                2 => ScriptedModel.CallTool("mcp_test_issues_create", new Dictionary<string, object?> { ["title"] = "修复登录" }, text: "需要建一个缺陷单。"),
                _ => ScriptedModel.Reply("已提交。"),
            });
        };
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var agent = await StarterAgentAsync(client);
        agent.Permissions.Should().Contain("integrations.tools.use");
        await PostAsync<TenantSecretDto>(client, "/api/integrations/secrets", new CreateTenantSecretRequest { Name = "MCP_TOKEN", Value = Token });
        var mcp = await PostAsync<McpServerDto>(client, "/api/integrations/mcp-servers", Server(server.Url));
        await PutAsync<McpServerDto>(client, $"/api/integrations/mcp-servers/{mcp.Id}/tools", new UpdateMcpToolRequest { Tool = "echo", ReadOnly = true });

        await PostAsync<WakeupDto>(client, $"/api/agents/{agent.Id}/wake", new WakeAgentRequest { Reason = "处理缺陷" });
        var runs = await EventuallyAsync(RunsAsync, r => r.Count == 1 && r[0].Status == RunStatus.Succeeded);

        var run = await GetAsync<RunDetailDto>(client, $"/api/runs/{runs[0].Id}");
        var echoResult = run.Events.First(e => e.Type == RunEventType.ToolResult);
        echoResult.Data.GetProperty("actionKey").GetString().Should().Be("mcp.test.echo");
        echoResult.Data.GetProperty("output").GetProperty("content").GetString().Should().Be("回声：查一下");
        run.Events.Should().Contain(e => e.Type == RunEventType.ApprovalRequested);
        server.Calls.Should().Equal("echo");

        var approval = (await GetAsync<List<ApprovalDto>>(client, "/api/approvals?status=pending")).Should().ContainSingle().Subject;
        approval.ActionKey.Should().Be("mcp.test.issues_create");
        approval.ActionTitle.Should().Be("测试服务 · issues/create");
        approval.Rationale.Should().Be("需要建一个缺陷单。");

        var decided = await PostAsync<ApprovalDto>(client, $"/api/approvals/{approval.Id}/decision", new DecideApprovalRequest { Approve = true });
        decided.ExecutionError.Should().BeNull();
        decided.Result!.Value.GetProperty("structured").GetProperty("title").GetString().Should().Be("修复登录");
        server.Calls.Should().Equal("echo", "issues/create");
    }

    [Fact]
    public async Task Workflow_http_headers_use_secrets_without_recording_them()
    {
        await using var server = await TestMcpServer.StartAsync();
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        await PostAsync<TenantSecretDto>(client, "/api/integrations/secrets", new CreateTenantSecretRequest { Name = "MCP_TOKEN", Value = Token });

        var workflow = await PostAsync<WorkflowDto>(client, "/api/workflows", new SaveWorkflowRequest
        {
            Name = "带令牌回调",
            Trigger = new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Manual },
            Steps =
            [
                new WorkflowStep
                {
                    Key = "call",
                    Name = "回调",
                    Kind = WorkflowStepKind.Http,
                    Method = "POST",
                    Url = $"{server.BaseUrl}/echo",
                    Headers = new Dictionary<string, string>
                    {
                        ["Authorization"] = "Bearer {{ secrets.MCP_TOKEN }}",
                        ["X-Trace"] = "{{ input.trace }}",
                    },
                    Body = Json("""{ "trace": "{{ input.trace }}" }"""),
                },
            ],
        });

        // 触发数据里混进的引用写法只是普通文字，不会被当成密钥引用。
        var run = await RunAsync(client, workflow.Id, """{ "trace": "{{ secrets.MCP_TOKEN }}" }""");

        server.Authorizations.Should().Equal($"Bearer {Token}");
        server.Traces.Should().Equal("{{ secrets.MCP_TOKEN }}");
        var headers = run.StepRuns[0].Input!.Value.GetProperty("headers");
        headers.GetProperty("Authorization").GetString().Should().Be("Bearer {{ secrets.MCP_TOKEN }}");

        // 对方回显的令牌在记录响应前被抹掉。
        run.StepRuns[0].Output!.Value.GetProperty("body").GetProperty("authorization").GetString().Should().Be("Bearer ***");
        PlatformJson.Serialize(run).Should().NotContain(Token);
    }

    [Fact]
    public async Task Secret_references_are_validated_when_saving_workflows()
    {
        var owner = await RegisterAsync();
        using var ownerClient = Client(owner.AccessToken);
        await PostAsync<TenantSecretDto>(ownerClient, "/api/integrations/secrets", new CreateTenantSecretRequest { Name = "MCP_TOKEN", Value = Token });
        var roles = await GetAsync<List<RoleDto>>(ownerClient, "/api/roles");
        using var memberClient = Client(await AddMemberAsync(ownerClient, "member@example.com", roles.Single(r => r.Key == "member").Id));

        async Task<Dictionary<string, string[]>> ErrorsAsync(HttpClient client, WorkflowStep step)
        {
            var response = await client.PostAsJsonAsync("/api/workflows", new SaveWorkflowRequest
            {
                Name = "校验",
                Trigger = new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Manual },
                Steps = [step],
            }, PlatformJson.Options, Ct);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            return problem.GetProperty("errors").Deserialize<Dictionary<string, string[]>>()!;
        }

        static WorkflowStep Http(Dictionary<string, string>? headers = null, string? body = null) => new()
        {
            Key = "call",
            Name = "回调",
            Kind = WorkflowStepKind.Http,
            Method = "POST",
            Url = "https://example.com/hook",
            Headers = headers,
            Body = body is null ? null : Json(body),
        };

        (await ErrorsAsync(ownerClient, Http(body: """{ "token": "{{ secrets.MCP_TOKEN }}" }""")))["steps[0].body"]
            .Should().ContainSingle().Which.Should().Contain("只能在 HTTP 请求头里引用");
        (await ErrorsAsync(ownerClient, Http(new() { ["Authorization"] = "Bearer {{ secrets.MISSING }}" })))["steps[0].headers"]
            .Should().ContainSingle().Which.Should().Contain("MISSING 不存在");
        (await ErrorsAsync(ownerClient, Http(new() { ["Authorization"] = "Bearer {{ lower(secrets.MCP_TOKEN) }}" })))["steps[0].headers"]
            .Should().ContainSingle().Which.Should().Contain("单独写成");

        // 成员能管理工作流，但没被授权使用密钥：引用了就能把密钥发往任意地址。
        (await ErrorsAsync(memberClient, Http(new() { ["Authorization"] = "Bearer {{ secrets.MCP_TOKEN }}" })))["steps[0].headers"]
            .Should().ContainSingle().Which.Should().Contain("integrations.secrets.use");
    }

    [Fact]
    public async Task Workflows_call_external_tools_and_references_block_deletion()
    {
        await using var server = await TestMcpServer.StartAsync();
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var secret = await PostAsync<TenantSecretDto>(client, "/api/integrations/secrets", new CreateTenantSecretRequest { Name = "MCP_TOKEN", Value = Token });
        var mcp = await PostAsync<McpServerDto>(client, "/api/integrations/mcp-servers", Server(server.Url));
        var workflow = await PostAsync<WorkflowDto>(client, "/api/workflows", new SaveWorkflowRequest
        {
            Name = "外部工具",
            Trigger = new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Manual },
            Steps = [new WorkflowStep { Key = "echo", Name = "回声", Kind = WorkflowStepKind.Action, ActionKey = "mcp.test.echo", Input = Json("""{ "text": "{{ input.text }}" }""") }],
        });

        var run = await RunAsync(client, workflow.Id, """{ "text": "工作流" }""");
        run.StepRuns[0].Output!.Value.GetProperty("content").GetString().Should().Be("回声：工作流");

        var deleteSecret = await client.DeleteAsync(new Uri($"/api/integrations/secrets/{secret.Id}", UriKind.Relative), Ct);
        deleteSecret.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(deleteSecret)).Should().Be("tenant_secret.in_use");

        var deleteServer = await client.DeleteAsync(new Uri($"/api/integrations/mcp-servers/{mcp.Id}", UriKind.Relative), Ct);
        deleteServer.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(deleteServer)).Should().Be("mcp_server.in_use");

        (await client.DeleteAsync(new Uri($"/api/workflows/{workflow.Id}", UriKind.Relative), Ct)).EnsureSuccessStatusCode();
        (await client.DeleteAsync(new Uri($"/api/integrations/mcp-servers/{mcp.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.DeleteAsync(new Uri($"/api/integrations/secrets/{secret.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Integrations_respect_permissions_and_tenants()
    {
        await using var server = await TestMcpServer.StartAsync();
        var owner = await RegisterAsync();
        using var ownerClient = Client(owner.AccessToken);
        var roles = await GetAsync<List<RoleDto>>(ownerClient, "/api/roles");
        using var viewerClient = Client(await AddMemberAsync(ownerClient, "viewer@example.com", roles.Single(r => r.Key == "viewer").Id));
        var mcp = await PostAsync<McpServerDto>(ownerClient, "/api/integrations/mcp-servers", Server(server.Url) with
        {
            Headers = [new McpServerHeader("X-Api-Key", "literal-key"), new McpServerHeader("X-Version", "2")],
        });

        // 只读成员看得到服务，但敏感请求头里直接写的值被遮住；不能修改，也调用不了工具。
        var seen = await GetAsync<McpServerDto>(viewerClient, $"/api/integrations/mcp-servers/{mcp.Id}");
        seen.Headers.Should().Equal(new McpServerHeader("X-Api-Key", "***"), new McpServerHeader("X-Version", "2"));
        (await viewerClient.PostAsJsonAsync("/api/integrations/secrets", new CreateTenantSecretRequest { Name = "X", Value = "y" }, PlatformJson.Options, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PostAsync<InvokeActionResponse>(viewerClient, "/api/actions/mcp.test.echo/invoke", new { text = "x" })).Status.Should().Be("forbidden");

        // 别的空间看不到。
        var other = await RegisterAsync("other@example.com", "别的空间");
        using var otherClient = Client(other.AccessToken);
        (await GetAsync<List<McpServerDto>>(otherClient, "/api/integrations/mcp-servers")).Should().BeEmpty();
        (await otherClient.GetAsync(new Uri($"/api/integrations/mcp-servers/{mcp.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync<List<ActionDescriptorDto>>(otherClient, "/api/actions")).Should().NotContain(a => a.Key.StartsWith("mcp.", StringComparison.Ordinal));

        // 标识重复、引用不存在的密钥都被挡回。
        var duplicate = await ownerClient.PostAsJsonAsync("/api/integrations/mcp-servers", Server(server.Url), PlatformJson.Options, Ct);
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var missingSecret = await ownerClient.PostAsJsonAsync("/api/integrations/mcp-servers", Server(server.Url) with { Key = "other" }, PlatformJson.Options, Ct);
        missingSecret.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unreachable_server_is_saved_with_the_error_and_can_be_refreshed()
    {
        await using var server = await TestMcpServer.StartAsync();
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        await PostAsync<TenantSecretDto>(client, "/api/integrations/secrets", new CreateTenantSecretRequest { Name = "MCP_TOKEN", Value = Token });

        var broken = await PostAsync<McpServerDto>(client, "/api/integrations/mcp-servers", Server($"{server.BaseUrl}/missing"));
        broken.Tools.Should().BeEmpty();
        broken.LastError.Should().NotBeNullOrEmpty();

        var fixedServer = await PutAsync<McpServerDto>(client, $"/api/integrations/mcp-servers/{broken.Id}", Server(server.Url));
        fixedServer.LastError.Should().BeNull();
        fixedServer.Tools.Should().HaveCount(4);

        var refreshed = await PostAsync<McpServerDto>(client, $"/api/integrations/mcp-servers/{broken.Id}/refresh", new { });
        refreshed.Tools.Should().HaveCount(4);
        refreshed.ToolsRefreshedAt.Should().BeOnOrAfter(fixedServer.ToolsRefreshedAt!.Value);
    }

    [Fact]
    public async Task Referencing_secrets_in_mcp_headers_requires_secret_use_permission()
    {
        await using var server = await TestMcpServer.StartAsync();
        var owner = await RegisterAsync();
        using var ownerClient = Client(owner.AccessToken);
        await PostAsync<TenantSecretDto>(ownerClient, "/api/integrations/secrets", new CreateTenantSecretRequest { Name = "MCP_TOKEN", Value = Token });
        await PostAsync<TenantSecretDto>(ownerClient, "/api/integrations/secrets", new CreateTenantSecretRequest { Name = "OTHER_TOKEN", Value = "other-token-value" });
        var mcp = await PostAsync<McpServerDto>(ownerClient, "/api/integrations/mcp-servers", Server(server.Url));

        // 能管理集成、但没被授权使用密钥：引用了就能把密钥发往自己的地址，等同于读取。
        var role = await PostAsync<RoleDto>(ownerClient, "/api/roles", new SaveRoleRequest
        {
            Key = "integrator",
            Name = "集成管理员",
            Features = ["integrations.view", "integrations.manage"],
        });
        using var client = Client(await AddMemberAsync(ownerClient, "integrator@example.com", role.Id));
        var calls = server.Authorizations.Count;

        async Task<string[]> HeaderErrorsAsync(HttpResponseMessage response)
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            return problem.GetProperty("errors").GetProperty("headers").Deserialize<string[]>()!;
        }

        (await HeaderErrorsAsync(await client.PostAsJsonAsync("/api/integrations/mcp-servers", Server(server.Url) with { Key = "mine" }, PlatformJson.Options, Ct)))
            .Should().ContainSingle().Which.Should().Contain("integrations.secrets.use");
        (await HeaderErrorsAsync(await client.PutAsJsonAsync($"/api/integrations/mcp-servers/{mcp.Id}", Server($"{server.BaseUrl}/other"), PlatformJson.Options, Ct)))
            .Should().ContainSingle().Which.Should().Contain("integrations.secrets.use");
        var addedReference = Server(server.Url) with { Headers = [.. Server(server.Url).Headers, new McpServerHeader("X-Other", "{{ secrets.OTHER_TOKEN }}")] };
        (await HeaderErrorsAsync(await client.PutAsJsonAsync($"/api/integrations/mcp-servers/{mcp.Id}", addedReference, PlatformJson.Options, Ct)))
            .Should().ContainSingle().Which.Should().Contain("integrations.secrets.use");
        server.Authorizations.Count.Should().Be(calls, "被挡回的请求不会连外部服务");

        // 地址和引用不变，只改其他设置可以。
        var renamed = await PutAsync<McpServerDto>(client, $"/api/integrations/mcp-servers/{mcp.Id}", Server(server.Url) with { Name = "改名后的服务" });
        renamed.Name.Should().Be("改名后的服务");
    }

    [Fact]
    public async Task Mcp_urls_cannot_carry_credentials()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);

        var response = await client.PostAsJsonAsync("/api/integrations/mcp-servers", Server("https://user:p4ss@example.com/mcp") with { Headers = [] }, PlatformJson.Options, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("errors").GetProperty("url")[0].GetString().Should().Contain("不能包含用户名和密码");
    }

    [Fact]
    public async Task Discovery_stops_reading_once_enough_tools_are_collected()
    {
        await using var server = await TestMcpServer.StartAsync(extraTools: 450);
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        await PostAsync<TenantSecretDto>(client, "/api/integrations/secrets", new CreateTenantSecretRequest { Name = "MCP_TOKEN", Value = Token });

        var mcp = await PostAsync<McpServerDto>(client, "/api/integrations/mcp-servers", Server(server.Url));

        mcp.Tools.Should().HaveCount(200);
        mcp.LastError.Should().Contain("超过 200 个");
        server.ListPages.Should().Be(3, "每页 100 个，读到第 201 个就停，不再往后翻");
    }

    /// <summary>
    /// 平台自己的 /mcp 列出的工具名。
    /// </summary>
    private async Task<List<string>> PlatformToolsAsync(string token)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(Client().BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            Client(token),
            ownsHttpClient: true);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: Ct);
        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        return [.. tools.Select(t => t.Name)];
    }

    private static SaveMcpServerRequest Server(string url) => new()
    {
        Key = "test",
        Name = "测试服务",
        Url = url,
        Headers = [new McpServerHeader("Authorization", "Bearer {{ secrets.MCP_TOKEN }}")],
    };

    private static async Task<WorkflowRunDto> RunAsync(HttpClient client, Guid workflowId, string input)
    {
        var started = await PostAsync<WorkflowRunSummaryDto>(client, $"/api/workflows/{workflowId}/runs", new StartWorkflowRunRequest { Input = Json(input) });
        var run = await EventuallyAsync(
            () => GetAsync<WorkflowRunDto>(client, $"/api/workflows/runs/{started.Id}"),
            r => r.Summary.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed or WorkflowRunStatus.Cancelled);
        run.Summary.Status.Should().Be(WorkflowRunStatus.Succeeded, "运行结果：{0}", PlatformJson.Serialize(run));
        return run;
    }

    /// <summary>
    /// 测试用的外部 MCP 服务（有状态的 Streamable HTTP），另带一个普通的 /echo 接口给 HTTP 步骤用。
    /// </summary>
    private sealed class TestMcpServer : IAsyncDisposable
    {
        private static readonly JsonElement TextSchema = JsonDocument.Parse("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""").RootElement.Clone();
        private static readonly JsonElement TitleSchema = JsonDocument.Parse("""{"type":"object","properties":{"title":{"type":"string"}}}""").RootElement.Clone();

        private readonly WebApplication _app;

        private TestMcpServer(WebApplication app, string baseUrl)
        {
            _app = app;
            BaseUrl = baseUrl;
        }

        public string BaseUrl { get; }

        public string Url => $"{BaseUrl}/mcp";

        public ConcurrentQueue<string> Authorizations { get; } = new();

        public ConcurrentQueue<string> Traces { get; } = new();

        public ConcurrentQueue<string> Calls { get; } = new();

        private int _listPages;

        /// <summary>
        /// 被请求了几页工具列表。
        /// </summary>
        public int ListPages => Volatile.Read(ref _listPages);

        /// <param name="extraTools">另外再提供多少个工具，每页 100 个分页返回。</param>
        public static async Task<TestMcpServer> StartAsync(int extraTools = 0)
        {
            const int pageSize = 100;
            TestMcpServer? server = null;
            List<Tool> tools =
            [
                new Tool { Name = "echo", Title = "回声", Description = "原样返回文字。", InputSchema = TextSchema, Annotations = new ToolAnnotations { ReadOnlyHint = true } },
                new Tool { Name = "issues/create", Description = "创建缺陷单。", InputSchema = TitleSchema },
                new Tool { Name = "explode", Description = "总是失败。", InputSchema = TitleSchema },
                new Tool { Name = "whoami", Description = "回显收到的令牌。", InputSchema = TitleSchema },
                .. Enumerable.Range(1, extraTools).Select(i => new Tool { Name = $"extra{i}", Description = "批量工具。", InputSchema = TitleSchema }),
            ];
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddMcpServer(options => options.ServerInfo = new Implementation { Name = "test-server", Version = "1.0.0" })
                .WithHttpTransport()
                .WithListToolsHandler((context, _) =>
                {
                    Interlocked.Increment(ref server!._listPages);
                    var offset = int.TryParse(context.Params?.Cursor, out var cursor) ? cursor : 0;
                    return ValueTask.FromResult(new ListToolsResult
                    {
                        Tools = [.. tools.Skip(offset).Take(pageSize)],
                        NextCursor = offset + pageSize < tools.Count ? (offset + pageSize).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                    });
                })
                .WithCallToolHandler((context, _) =>
                {
                    var name = context.Params!.Name;
                    server!.Calls.Enqueue(name);
                    var args = context.Params.Arguments ?? new Dictionary<string, JsonElement>();
                    return ValueTask.FromResult(name switch
                    {
                        "echo" => new CallToolResult { Content = [new TextContentBlock { Text = $"回声：{args["text"].GetString()}" }] },
                        "whoami" => new CallToolResult
                        {
                            Content = [new TextContentBlock { Text = $"收到令牌：{server.Authorizations.Last()}" }],
                            StructuredContent = JsonSerializer.SerializeToElement(new { authorization = server.Authorizations.Last() }),
                        },
                        "issues/create" => new CallToolResult
                        {
                            Content = [new TextContentBlock { Text = "已创建" }],
                            StructuredContent = JsonSerializer.SerializeToElement(new { number = 42, title = args["title"].GetString() }),
                        },
                        _ => new CallToolResult { Content = [new TextContentBlock { Text = "仓库不存在" }], IsError = true },
                    });
                });
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/mcp") || context.Request.Path.StartsWithSegments("/echo"))
                {
                    server!.Authorizations.Enqueue(context.Request.Headers.Authorization.ToString());
                }

                await next(context);
            });
            app.MapMcp("/mcp");
            app.MapPost("/echo", (HttpContext context) =>
            {
                server!.Traces.Enqueue(context.Request.Headers["X-Trace"].ToString());
                return Results.Json(new { ok = true, authorization = context.Request.Headers.Authorization.ToString() });
            });
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            server = new TestMcpServer(app, address.TrimEnd('/'));
            return server;
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }
}
