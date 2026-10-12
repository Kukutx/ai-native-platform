using System.Net;
using System.Net.Http.Json;
using AiNative.Application.Common;
using AiNative.Contracts.Auth;
using AiNative.Contracts.Common;
using AiNative.Contracts.Platform;
using AiNative.Contracts.Work;
using AiNative.Domain.Identity;
using AiNative.IntegrationTests.Infrastructure;

namespace AiNative.IntegrationTests;

/// <summary>
/// 一个账号加入多个空间时的切换：只能切到自己有有效成员资格的空间，换到的令牌只看得到那个空间。
/// </summary>
public sealed class TenantSwitchTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Switching_needs_an_active_membership_and_the_new_token_only_sees_that_space()
    {
        var alice = await RegisterAsync("alice@example.com", "甲空间");
        var bob = await RegisterAsync("bob@example.com", "乙空间");
        using var aliceClient = Client(alice.AccessToken);
        using var bobClient = Client(bob.AccessToken);
        var home = alice.Session.Tenant.Id;
        var other = bob.Session.Tenant.Id;
        await PostAsync<WorkItemDto>(aliceClient, "/api/work-items", new CreateWorkItemRequest { Title = "甲的任务" });
        await PostAsync<WorkItemDto>(bobClient, "/api/work-items", new CreateWorkItemRequest { Title = "乙的任务" });

        // 不是成员不能切过去。
        (await SwitchAsync(aliceClient, other)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 被加入乙空间后可以切换：新令牌是普通成员身份，只看得到乙空间的数据；原令牌仍停在甲空间。
        var memberRole = (await GetAsync<List<RoleDto>>(bobClient, "/api/roles")).Single(r => r.Key == "member");
        var membership = await PostAsync<MemberDto>(bobClient, "/api/members", new AddMemberRequest
        {
            Email = "alice@example.com",
            DisplayName = "alice",
            RoleIds = [memberRole.Id],
        });
        var switched = await PostAsync<AuthResponse>(aliceClient, "/api/auth/switch-tenant", new SwitchTenantRequest { TenantId = other });
        switched.Session.Tenant.Id.Should().Be(other);
        switched.Session.Tenant.IsOwner.Should().BeFalse();
        switched.Session.Tenants.Select(t => t.Id).Should().BeEquivalentTo([home, other]);
        switched.Session.Features.Should().NotContain("*").And.Contain("tasks.*");

        using var switchedClient = Client(switched.AccessToken);
        (await GetAsync<SessionDto>(switchedClient, "/api/auth/session")).Tenant.Id.Should().Be(other);
        (await GetAsync<PagedResult<WorkItemDto>>(switchedClient, "/api/work-items")).Items.Select(t => t.Title).Should().Equal("乙的任务");
        await PostAsync<WorkItemDto>(switchedClient, "/api/work-items", new CreateWorkItemRequest { Title = "在乙空间建的任务" });
        (await GetAsync<PagedResult<WorkItemDto>>(aliceClient, "/api/work-items")).Items.Select(t => t.Title).Should().Equal("甲的任务");
        (await GetAsync<PagedResult<WorkItemDto>>(bobClient, "/api/work-items")).Total.Should().Be(2);

        // 成员资格被停用后不能再切过去，也不能直接登录进去；不指定空间登录时落到仍然有效的空间。
        await PutAsync<MemberDto>(bobClient, $"/api/members/{membership.MembershipId}", new UpdateMemberRequest
        {
            RoleIds = membership.RoleIds,
            Status = MembershipStatus.Suspended,
        });
        (await SwitchAsync(aliceClient, other)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var anonymous = Client();
        var direct = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = "alice@example.com", Password = "Passw0rd!x", TenantId = other }, PlatformJson.Options, Ct);
        direct.IsSuccessStatusCode.Should().BeFalse();
        var login = await PostAsync<AuthResponse>(anonymous, "/api/auth/login", new LoginRequest { Email = "alice@example.com", Password = "Passw0rd!x" });
        login.Session.Tenant.Id.Should().Be(home);
        login.Session.Tenants.Should().ContainSingle();
    }

    private static Task<HttpResponseMessage> SwitchAsync(HttpClient client, Guid tenantId)
        => client.PostAsJsonAsync("/api/auth/switch-tenant", new SwitchTenantRequest { TenantId = tenantId }, PlatformJson.Options, Ct);
}
