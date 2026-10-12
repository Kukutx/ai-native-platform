using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AiNative.Application.Common;
using AiNative.Contracts.Agents;
using AiNative.Contracts.Auth;
using AiNative.Contracts.Common;
using AiNative.Contracts.Governance;
using AiNative.Contracts.Integrations;
using AiNative.Contracts.Platform;
using AiNative.Contracts.Workflows;
using AiNative.Domain.Agents;
using AiNative.Domain.Identity;
using AiNative.Domain.Workflows;
using AiNative.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace AiNative.IntegrationTests;

/// <summary>
/// 安全边界：API 密钥不能换发登录令牌、修改密码让旧令牌失效、授权不能超出自己、智能体运行工作流要审批、
/// 敏感请求头打码、带密钥的请求只发往写明的主机。
/// </summary>
public sealed class SecurityTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Api_key_cannot_mint_login_tokens_or_use_realtime()
    {
        var auth = await RegisterAsync();
        using var owner = Client(auth.AccessToken);
        var key = await PostAsync<CreatedApiKeyDto>(owner, "/api/api-keys", new CreateApiKeyRequest { Name = "只读脚本", ReadOnly = true });
        using var viaKey = Client(key.Token);

        var switched = await viaKey.PostAsJsonAsync("/api/auth/switch-tenant", new SwitchTenantRequest { TenantId = auth.Session.Tenant.Id }, PlatformJson.Options, Ct);
        var refreshed = await viaKey.PostAsync(new Uri("/api/auth/refresh", UriKind.Relative), null, Ct);
        var session = await viaKey.GetAsync(new Uri("/api/auth/session", UriKind.Relative), Ct);
        var password = await viaKey.PostAsJsonAsync("/api/auth/password", new ChangePasswordRequest { CurrentPassword = "Passw0rd!x", NewPassword = "N3w-passw0rd" }, PlatformJson.Options, Ct);

        switched.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CodeAsync(switched)).Should().Be("auth.api_key_not_allowed");
        refreshed.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        session.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        password.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 实时通道按成员身份解析权限，套不上密钥的范围限制，所以不接受密钥。
        await using var hub = Hub(key.Token);
        var connect = () => hub.StartAsync(Ct);
        await connect.Should().ThrowAsync<HttpRequestException>();

        await using var loggedIn = Hub(auth.AccessToken);
        await loggedIn.StartAsync(Ct);
        loggedIn.State.Should().Be(HubConnectionState.Connected);
    }

    [Fact]
    public async Task Changing_password_revokes_previous_tokens()
    {
        var auth = await RegisterAsync();
        var otherDevice = await PostAsync<AuthResponse>(Client(), "/api/auth/login", new LoginRequest { Email = "owner@example.com", Password = "Passw0rd!x" });
        using var client = Client(auth.AccessToken);

        var wrong = await client.PostAsJsonAsync("/api/auth/password", new ChangePasswordRequest { CurrentPassword = "not-my-password", NewPassword = "N3w-passw0rd" }, PlatformJson.Options, Ct);
        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var changed = await PostAsync<AuthResponse>(client, "/api/auth/password", new ChangePasswordRequest { CurrentPassword = "Passw0rd!x", NewPassword = "N3w-passw0rd" });

        using var stale = Client(auth.AccessToken);
        using var staleOther = Client(otherDevice.AccessToken);
        using var fresh = Client(changed.AccessToken);
        (await stale.GetAsync(new Uri("/api/agents", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await staleOther.GetAsync(new Uri("/api/agents", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await fresh.GetAsync(new Uri("/api/agents", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        changed.Session.Tenant.Id.Should().Be(auth.Session.Tenant.Id);

        var oldLogin = await Client().PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = "owner@example.com", Password = "Passw0rd!x" }, PlatformJson.Options, Ct);
        oldLogin.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await PostAsync<AuthResponse>(Client(), "/api/auth/login", new LoginRequest { Email = "owner@example.com", Password = "N3w-passw0rd" });

        // 旧令牌也不能换发新令牌。
        (await stale.PostAsync(new Uri("/api/auth/refresh", UriKind.Relative), null, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var renewed = await PostAsync<AuthResponse>(fresh, "/api/auth/refresh", new { });
        renewed.Session.User.Email.Should().Be("owner@example.com");
    }

    [Fact]
    public async Task Grants_cannot_exceed_the_granters_own_permissions()
    {
        var auth = await RegisterAsync();
        using var owner = Client(auth.AccessToken);
        var delegatedFeatures = new[] { "platform.members.view", "platform.members.manage", "platform.roles.manage", "agents.view", "agents.manage", "tasks.view" };
        var delegated = await PostAsync<RoleDto>(owner, "/api/roles", new SaveRoleRequest { Key = "hr", Name = "人事", Features = delegatedFeatures });
        using var hr = Client(await AddMemberAsync(owner, "hr@example.com", delegated.Id));
        var roles = await GetAsync<List<RoleDto>>(owner, "/api/roles");
        var admin = roles.Single(r => r.Key == "admin");
        var me = (await GetAsync<List<MemberDto>>(owner, "/api/members")).Single(m => m.Email == "hr@example.com");

        var wildcardRole = await hr.PostAsJsonAsync("/api/roles", new SaveRoleRequest { Key = "all", Name = "全部", Features = ["*"] }, PlatformJson.Options, Ct);
        var widenOwnRole = await hr.PutAsJsonAsync($"/api/roles/{delegated.Id}", new SaveRoleRequest { Key = "hr", Name = "人事", Features = [.. delegatedFeatures, "tasks.manage"] }, PlatformJson.Options, Ct);
        var selfPromote = await hr.PutAsJsonAsync($"/api/members/{me.MembershipId}", new UpdateMemberRequest { RoleIds = [delegated.Id, admin.Id], Status = MembershipStatus.Active }, PlatformJson.Options, Ct);
        var superAgent = await hr.PostAsJsonAsync("/api/agents", new SaveAgentRequest { Name = "全能", Title = "代办", Permissions = ["crm.manage"] }, PlatformJson.Options, Ct);

        foreach (var response in new[] { wildcardRole, widenOwnRole, selfPromote, superAgent })
        {
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await CodeAsync(response)).Should().Be("access.grant_exceeds_own");
        }

        // 没指定权限的智能体只拿到默认权限里自己能授予的部分；改名不新增权限，管理员角色已有的 * 不受限制；收回权限也不受限制。
        var helper = await PostAsync<AgentDto>(hr, "/api/agents", new SaveAgentRequest { Name = "助手", Title = "秘书" });
        helper.Permissions.Should().BeEquivalentTo("agents.view", "tasks.view");
        await PutAsync<RoleDto>(hr, $"/api/roles/{admin.Id}", new SaveRoleRequest { Key = "admin", Name = "超级管理员", Features = admin.Features });
        await PutAsync<RoleDto>(hr, $"/api/roles/{delegated.Id}", new SaveRoleRequest { Key = "hr", Name = "人事", Features = ["platform.members.view", "platform.roles.manage"] });

        // 所有者拥有全部权限，不受限制。
        await PostAsync<AgentDto>(owner, "/api/agents", new SaveAgentRequest { Name = "全能", Title = "代办", Permissions = ["crm.manage"] });
    }

    [Fact]
    public async Task Agent_running_a_workflow_needs_approval()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var workflow = await PostAsync<WorkflowDto>(client, "/api/workflows", new SaveWorkflowRequest
        {
            Name = "清理数据",
            Trigger = new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Manual },
            Steps = [new WorkflowStep { Key = "wait", Name = "等待", Kind = WorkflowStepKind.Delay, DelaySeconds = 1 }],
        });
        var requested = false;
        Factory.Script.Handler = (messages, _) =>
        {
            if (!requested && !ScriptedModel.HasToolResult(messages))
            {
                requested = true;
                return Task.FromResult(ScriptedModel.CallTool("workflows_run", new Dictionary<string, object?> { ["workflowId"] = workflow.Id }, text: "按计划清理。"));
            }

            return Task.FromResult(ScriptedModel.Reply("已提交。"));
        };
        var lead = await StarterAgentAsync(client);

        await PostAsync<WakeupDto>(client, $"/api/agents/{lead.Id}/wake", new WakeAgentRequest { Reason = "清理" });
        await EventuallyAsync(RunsAsync, r => r.Count == 1 && r[0].Status == RunStatus.Succeeded);

        var approval = (await GetAsync<List<ApprovalDto>>(client, "/api/approvals?status=pending")).Should().ContainSingle().Subject;
        approval.ActionKey.Should().Be("workflows.run");
        (await GetAsync<PagedResult<WorkflowRunSummaryDto>>(client, "/api/workflows/runs")).Total.Should().Be(0, "审批前不运行");

        var decided = await PostAsync<ApprovalDto>(client, $"/api/approvals/{approval.Id}/decision", new DecideApprovalRequest { Approve = true });
        decided.ExecutionError.Should().BeNull();
        (await GetAsync<PagedResult<WorkflowRunSummaryDto>>(client, "/api/workflows/runs")).Total.Should().Be(1);
    }

    [Fact]
    public async Task Sensitive_workflow_headers_are_masked_for_viewers_and_secrets_need_a_fixed_host()
    {
        var auth = await RegisterAsync();
        using var owner = Client(auth.AccessToken);
        var roles = await GetAsync<List<RoleDto>>(owner, "/api/roles");
        using var viewer = Client(await AddMemberAsync(owner, "viewer@example.com", roles.Single(r => r.Key == "viewer").Id));
        var workflow = await PostAsync<WorkflowDto>(owner, "/api/workflows", new SaveWorkflowRequest
        {
            Name = "回调",
            Trigger = new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Manual },
            Steps =
            [
                new WorkflowStep
                {
                    Key = "call",
                    Name = "回调",
                    Kind = WorkflowStepKind.Http,
                    Method = "POST",
                    Url = "https://example.com/hook",
                    Headers = new Dictionary<string, string> { ["X-Api-Key"] = "literal-key-value", ["X-Source"] = "ainative" },
                },
            ],
        });

        workflow.Steps[0].Headers!["X-Api-Key"].Should().Be("literal-key-value", "能修改工作流的人看到原值，保存时才不会丢");
        var seen = await GetAsync<WorkflowDto>(viewer, $"/api/workflows/{workflow.Id}");
        seen.Steps[0].Headers!["X-Api-Key"].Should().Be("***");
        seen.Steps[0].Headers!["X-Source"].Should().Be("ainative");
        PlatformJson.Serialize(await GetAsync<List<WorkflowDto>>(viewer, "/api/workflows")).Should().NotContain("literal-key-value");

        // 请求头引用密钥时，主机不能由模板生成，否则触发数据可以把密钥引到别的主机。
        await PostAsync<TenantSecretDto>(owner, "/api/integrations/secrets", new CreateTenantSecretRequest { Name = "API_TOKEN", Value = "s3cr3t" });
        foreach (var url in new[] { "{{ input.url }}", "https://{{ input.host }}/hook", "https://api.example.com{{ input.path }}" })
        {
            var response = await owner.PostAsJsonAsync("/api/workflows", new SaveWorkflowRequest
            {
                Name = "带密钥",
                Trigger = new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Manual },
                Steps = [new WorkflowStep { Key = "call", Name = "回调", Kind = WorkflowStepKind.Http, Url = url, Headers = new() { ["Authorization"] = "Bearer {{ secrets.API_TOKEN }}" } }],
            }, PlatformJson.Options, Ct);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, url);
            var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");
            errors.GetProperty("steps[0].url")[0].GetString().Should().Contain("协议和主机要直接写明");
        }

        await PostAsync<WorkflowDto>(owner, "/api/workflows", new SaveWorkflowRequest
        {
            Name = "带密钥",
            Trigger = new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Manual },
            Steps = [new WorkflowStep { Key = "call", Name = "回调", Kind = WorkflowStepKind.Http, Url = "https://api.example.com/items/{{ input.id }}", Headers = new() { ["Authorization"] = "Bearer {{ secrets.API_TOKEN }}" } }],
        });
    }

    [Fact]
    public async Task Webhook_verification_headers_work_in_filters_but_are_not_recorded()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var workflow = await PostAsync<WorkflowDto>(client, "/api/workflows", new SaveWorkflowRequest
        {
            Name = "代码推送",
            IsEnabled = true,
            Trigger = new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Webhook, Filter = "trigger.headers['x-gitlab-token'] == 'hook-secret'" },
            Steps = [new WorkflowStep { Key = "wait", Name = "等待", Kind = WorkflowStepKind.Delay, DelaySeconds = 1 }],
        });
        using var anonymous = Client();

        async Task<WebhookAcceptedDto> HookAsync(string token)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/hooks/workflows/{workflow.WebhookToken}")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            message.Headers.Add("X-Gitlab-Token", token);
            var response = await anonymous.SendAsync(message, Ct);
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            return (await response.Content.ReadFromJsonAsync<WebhookAcceptedDto>(PlatformJson.Options, Ct))!;
        }

        (await HookAsync("wrong")).Started.Should().BeFalse();
        var accepted = await HookAsync("hook-secret");

        accepted.Started.Should().BeTrue();
        var run = await GetAsync<WorkflowRunDto>(client, $"/api/workflows/runs/{accepted.RunId}");
        run.Trigger.GetProperty("headers").GetProperty("x-gitlab-token").GetString().Should().Be("***");
    }

    private HubConnection Hub(string token) => new HubConnectionBuilder()
        .WithUrl(new Uri(Factory.Server.BaseAddress, "/hubs/realtime"), options =>
        {
            options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
            options.Transports = HttpTransportType.LongPolling;
            options.AccessTokenProvider = () => Task.FromResult<string?>(token);
        })
        .Build();
}

/// <summary>
/// 登录防护：同一邮箱连续输错后暂时拒绝；同一 IP 的登录、注册请求有频率上限。
/// </summary>
public sealed class LoginProtectionTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    protected override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>
    {
        ["Auth:LoginThrottle:MaxFailures"] = "3",
        ["Auth:RateLimitPerMinute"] = "8",
    };

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_email_temporarily()
    {
        await RegisterAsync();
        using var client = Client();

        for (var i = 0; i < 3; i++)
        {
            (await LoginAsync(client, "owner@example.com", "wrong-password")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var locked = await LoginAsync(client, "owner@example.com", "Passw0rd!x");
        locked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await CodeAsync(locked)).Should().Be("auth.too_many_attempts");

        // 不存在的邮箱同样返回「邮箱或密码不正确」，不透露是否注册过。
        var unknown = await LoginAsync(client, "nobody@example.com", "whatever-password");
        unknown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Auth_endpoints_are_rate_limited_per_client()
    {
        using var client = Client();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 9; i++)
        {
            statuses.Add((await LoginAsync(client, $"user{i}@example.com", "whatever-password")).StatusCode);
        }

        statuses.Take(8).Should().AllSatisfy(s => s.Should().Be(HttpStatusCode.Unauthorized));
        statuses[8].Should().Be(HttpStatusCode.TooManyRequests);
        var limited = await LoginAsync(client, "user9@example.com", "whatever-password");
        (await CodeAsync(limited)).Should().Be("rate_limited");
        limited.Headers.RetryAfter.Should().NotBeNull();
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password)
        => client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password }, PlatformJson.Options, Ct);
}
