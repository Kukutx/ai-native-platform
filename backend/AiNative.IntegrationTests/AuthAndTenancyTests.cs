using System.Net;
using System.Net.Http.Json;
using AiNative.Application.Common;
using AiNative.Contracts.Auth;
using AiNative.Contracts.Common;
using AiNative.Contracts.Platform;
using AiNative.Contracts.Work;
using AiNative.IntegrationTests.Infrastructure;

namespace AiNative.IntegrationTests;

public sealed class AuthAndTenancyTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Register_creates_space_with_owner_access_and_starter_agent()
    {
        var auth = await RegisterAsync();

        auth.Session.Features.Should().Contain("*");
        auth.Session.Tenants.Should().ContainSingle().Which.IsOwner.Should().BeTrue();
        auth.Session.EnabledModules.Should().Contain(["platform", "agents", "chat"]);

        using var client = Client(auth.AccessToken);
        var agent = await StarterAgentAsync(client);
        agent.Name.Should().Be("小智");
        agent.ModelProvider.Should().Be(ApiFactory.Provider);
    }

    [Fact]
    public async Task Login_rejects_wrong_password_and_anonymous_requests()
    {
        await RegisterAsync();
        using var client = Client();

        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = "owner@example.com", Password = "wrong-password" }, PlatformJson.Options, Ct);
        var anonymous = await client.GetAsync(new Uri("/api/agents", UriKind.Relative), Ct);

        login.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Spaces_cannot_see_each_others_data()
    {
        var alice = await RegisterAsync("alice@example.com", "A 公司");
        var bob = await RegisterAsync("bob@example.com", "B 公司");
        using var aliceClient = Client(alice.AccessToken);
        using var bobClient = Client(bob.AccessToken);

        var task = await PostAsync<WorkItemDto>(aliceClient, "/api/work-items", new CreateWorkItemRequest { Title = "机密任务" });

        var direct = await bobClient.GetAsync(new Uri($"/api/work-items/{task.Id}", UriKind.Relative), Ct);
        var list = await GetAsync<PagedResult<WorkItemDto>>(bobClient, "/api/work-items");
        var assignToForeignAgent = await bobClient.PostAsJsonAsync(
            "/api/work-items",
            new CreateWorkItemRequest { Title = "x", AssigneeAgentId = (await StarterAgentAsync(aliceClient)).Id },
            PlatformJson.Options,
            Ct);

        direct.StatusCode.Should().Be(HttpStatusCode.NotFound);
        list.Total.Should().Be(0);
        task.Key.Should().Be("T-1");
        assignToForeignAgent.IsSuccessStatusCode.Should().BeFalse();
    }

    [Fact]
    public async Task Role_features_are_enforced_on_every_request()
    {
        var owner = await RegisterAsync();
        using var ownerClient = Client(owner.AccessToken);
        var viewer = (await GetAsync<List<RoleDto>>(ownerClient, "/api/roles")).Single(r => r.Key == "viewer");
        await PostAsync<MemberDto>(ownerClient, "/api/members", new AddMemberRequest
        {
            Email = "viewer@example.com",
            DisplayName = "访客",
            Password = "Passw0rd!x",
            RoleIds = [viewer.Id],
        });

        var login = await PostAsync<AuthResponse>(Client(), "/api/auth/login", new LoginRequest { Email = "viewer@example.com", Password = "Passw0rd!x" });
        using var viewerClient = Client(login.AccessToken);

        var read = await viewerClient.GetAsync(new Uri("/api/work-items", UriKind.Relative), Ct);
        var write = await viewerClient.PostAsJsonAsync("/api/work-items", new CreateWorkItemRequest { Title = "不允许" }, PlatformJson.Options, Ct);
        var admin = await viewerClient.GetAsync(new Uri("/api/usage", UriKind.Relative), Ct);

        read.StatusCode.Should().Be(HttpStatusCode.OK);
        write.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        admin.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        login.Session.Features.Should().NotContain("*");
    }

    [Fact]
    public async Task Disabled_module_blocks_its_features()
    {
        var owner = await RegisterAsync();
        using var client = Client(owner.AccessToken);

        await PutAsync<ModuleDto>(client, "/api/modules/agents", new SetModuleEnabledRequest { Enabled = false });
        var blocked = await client.GetAsync(new Uri("/api/agents", UriKind.Relative), Ct);
        var core = await client.PutAsJsonAsync("/api/modules/platform", new SetModuleEnabledRequest { Enabled = false }, PlatformJson.Options, Ct);

        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        core.IsSuccessStatusCode.Should().BeFalse();
    }
}
