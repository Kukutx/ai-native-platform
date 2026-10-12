using System.Net;
using System.Net.Http.Json;
using AiNative.Application.Common;
using AiNative.Contracts.Agents;
using AiNative.Contracts.Auth;
using AiNative.Contracts.Common;
using AiNative.Contracts.Platform;
using AiNative.Contracts.Work;
using AiNative.Domain.Agents;
using AiNative.Domain.Identity;
using AiNative.IntegrationTests.Infrastructure;

namespace AiNative.IntegrationTests;

/// <summary>
/// 管理类接口：角色的修改与删除、组织架构、模型目录和智能体组织图。
/// </summary>
public sealed class AdminEndpointTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Editing_a_role_changes_what_its_holders_can_do_and_deleting_it_revokes_that_access()
    {
        var owner = await RegisterAsync();
        using var ownerClient = Client(owner.AccessToken);
        var role = await PostAsync<RoleDto>(ownerClient, "/api/roles", new SaveRoleRequest { Key = "reporter", Name = "汇报", Features = ["tasks.view"] });
        using var memberClient = Client(await AddMemberAsync(ownerClient, "member@example.com", role.Id));
        (await GetAsync<PagedResult<WorkItemDto>>(memberClient, "/api/work-items")).Total.Should().Be(0);
        (await CreateTaskAsync(memberClient, "改角色前")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 修改立即对持有该角色的成员生效（权限缓存随提交失效），重复的权限只保留一份。
        var updated = await PutAsync<RoleDto>(ownerClient, $"/api/roles/{role.Id}", new SaveRoleRequest
        {
            Key = "task-keeper",
            Name = " 任务管理 ",
            Description = "能建任务",
            Features = ["tasks.view", "tasks.manage", "tasks.manage"],
        });
        updated.Should().Match<RoleDto>(r => r.Key == "task-keeper" && r.Name == "任务管理" && r.Description == "能建任务" && !r.IsSystem);
        updated.Features.Should().Equal("tasks.view", "tasks.manage");
        (await CreateTaskAsync(memberClient, "改角色后")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync<SessionDto>(memberClient, "/api/auth/session")).Features.Should().Contain("tasks.manage");

        // 角色键不能和已有角色重复；内置角色不能删除。
        var taken = await ownerClient.PutAsJsonAsync($"/api/roles/{role.Id}", new SaveRoleRequest { Key = "viewer", Name = "撞键", Features = ["tasks.view"] }, PlatformJson.Options, Ct);
        taken.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(taken)).Should().Be("role.key_taken");
        var viewer = (await GetAsync<List<RoleDto>>(ownerClient, "/api/roles")).Single(r => r.Key == "viewer");
        var system = await ownerClient.DeleteAsync(new Uri($"/api/roles/{viewer.Id}", UriKind.Relative), Ct);
        system.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(system)).Should().Be("role.system");

        // 删除角色会从持有者身上摘掉它，对应的权限随即失效。
        (await ownerClient.DeleteAsync(new Uri($"/api/roles/{role.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GetAsync<List<RoleDto>>(ownerClient, "/api/roles")).Should().NotContain(r => r.Id == role.Id);
        (await GetAsync<List<MemberDto>>(ownerClient, "/api/members")).Single(m => m.Email == "member@example.com").RoleIds.Should().BeEmpty();
        (await memberClient.GetAsync(new Uri("/api/work-items", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ownerClient.DeleteAsync(new Uri($"/api/roles/{role.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Organizations_form_a_tree_within_the_space_and_members_can_be_placed_in_them()
    {
        var owner = await RegisterAsync();
        using var ownerClient = Client(owner.AccessToken);
        var memberRole = (await GetAsync<List<RoleDto>>(ownerClient, "/api/roles")).Single(r => r.Key == "member");
        using var memberClient = Client(await AddMemberAsync(ownerClient, "member@example.com", memberRole.Id));

        // 注册时建好以空间命名的根组织。
        var root = (await GetAsync<List<OrganizationDto>>(ownerClient, "/api/organizations")).Should().ContainSingle().Subject;
        root.Should().Match<OrganizationDto>(o => o.Name == "测试空间" && o.ParentId == null && o.Depth == 0 && o.Path == $"/{o.Id}/");

        var sales = await PostAsync<OrganizationDto>(ownerClient, "/api/organizations", new CreateOrganizationRequest { Name = " 销售部 ", ParentId = root.Id });
        sales.Should().Match<OrganizationDto>(o => o.Name == "销售部" && o.ParentId == root.Id && o.Depth == 1 && o.Path == $"{root.Path}{o.Id}/");
        var east = await PostAsync<OrganizationDto>(ownerClient, "/api/organizations", new CreateOrganizationRequest { Name = "华东组", ParentId = sales.Id });
        east.Should().Match<OrganizationDto>(o => o.Depth == 2 && o.Path == $"{sales.Path}{o.Id}/");
        var advisors = await PostAsync<OrganizationDto>(ownerClient, "/api/organizations", new CreateOrganizationRequest { Name = "顾问团" });
        advisors.Should().Match<OrganizationDto>(o => o.ParentId == null && o.Depth == 0);

        var missingParent = await ownerClient.PostAsJsonAsync("/api/organizations", new CreateOrganizationRequest { Name = "孤儿", ParentId = Guid.NewGuid() }, PlatformJson.Options, Ct);
        missingParent.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var blank = await ownerClient.PostAsJsonAsync("/api/organizations", new CreateOrganizationRequest { Name = " " }, PlatformJson.Options, Ct);
        blank.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 列表按路径排列，上级总在下级之前；普通成员能看不能建。
        var listed = await GetAsync<List<OrganizationDto>>(memberClient, "/api/organizations");
        listed.Should().HaveCount(4);
        listed.FindIndex(o => o.Id == root.Id).Should().BeLessThan(listed.FindIndex(o => o.Id == sales.Id));
        listed.FindIndex(o => o.Id == sales.Id).Should().BeLessThan(listed.FindIndex(o => o.Id == east.Id));
        (await memberClient.PostAsJsonAsync("/api/organizations", new CreateOrganizationRequest { Name = "自建" }, PlatformJson.Options, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var member = (await GetAsync<List<MemberDto>>(ownerClient, "/api/members")).Single(m => m.Email == "member@example.com");
        var placed = await PutAsync<MemberDto>(ownerClient, $"/api/members/{member.MembershipId}", new UpdateMemberRequest
        {
            RoleIds = member.RoleIds,
            OrganizationId = east.Id,
            Status = MembershipStatus.Active,
        });
        placed.OrganizationId.Should().Be(east.Id);

        // 别的空间既看不到这些组织，也不能挂到它们下面或把成员放进去。
        var other = await RegisterAsync("other@example.com", "别的空间");
        using var otherClient = Client(other.AccessToken);
        (await GetAsync<List<OrganizationDto>>(otherClient, "/api/organizations")).Should().ContainSingle().Which.Name.Should().Be("别的空间");
        (await otherClient.PostAsJsonAsync("/api/organizations", new CreateOrganizationRequest { Name = "渗透", ParentId = sales.Id }, PlatformJson.Options, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        var otherMember = await PostAsync<MemberDto>(otherClient, "/api/members", new AddMemberRequest
        {
            Email = "someone@example.com",
            DisplayName = "someone",
            Password = "Passw0rd!x",
            RoleIds = [(await GetAsync<List<RoleDto>>(otherClient, "/api/roles")).Single(r => r.Key == "viewer").Id],
        });
        var foreignOrg = await otherClient.PutAsJsonAsync($"/api/members/{otherMember.MembershipId}", new UpdateMemberRequest
        {
            RoleIds = otherMember.RoleIds,
            OrganizationId = east.Id,
            Status = MembershipStatus.Active,
        }, PlatformJson.Options, Ct);
        foreignOrg.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Model_catalog_shows_the_configured_default_to_whoever_can_view_agents()
    {
        var owner = await RegisterAsync();
        using var ownerClient = Client(owner.AccessToken);

        var catalog = await GetAsync<ModelCatalogDto>(ownerClient, "/api/models");

        (catalog.DefaultProvider, catalog.DefaultModel).Should().Be((ApiFactory.Provider, ApiFactory.Model));
        var provider = catalog.Providers.Should().ContainSingle(p => p.Name == ApiFactory.Provider).Subject;
        provider.Kind.Should().Be(ApiFactory.Provider);
        provider.Models.Should().Contain(ApiFactory.Model);
        provider.Configured.Should().BeTrue();

        var viewer = (await GetAsync<List<RoleDto>>(ownerClient, "/api/roles")).Single(r => r.Key == "viewer");
        using var viewerClient = Client(await AddMemberAsync(ownerClient, "viewer@example.com", viewer.Id));
        (await GetAsync<ModelCatalogDto>(viewerClient, "/api/models")).DefaultModel.Should().Be(ApiFactory.Model);
        var tasksOnly = await PostAsync<RoleDto>(ownerClient, "/api/roles", new SaveRoleRequest { Key = "tasks-only", Name = "只管任务", Features = ["tasks.view"] });
        using var tasksOnlyClient = Client(await AddMemberAsync(ownerClient, "tasks@example.com", tasksOnly.Id));
        (await tasksOnlyClient.GetAsync(new Uri("/api/models", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Org_chart_nests_reports_and_relinks_them_when_their_manager_is_terminated()
    {
        var owner = await RegisterAsync();
        using var ownerClient = Client(owner.AccessToken);
        var chief = await StarterAgentAsync(ownerClient);
        var manager = await PostAsync<AgentDto>(ownerClient, "/api/agents", new SaveAgentRequest { Name = "老王", Title = "运营经理", ReportsToId = chief.Id });
        var analyst = await PostAsync<AgentDto>(ownerClient, "/api/agents", new SaveAgentRequest { Name = "小李", Title = "分析师", ReportsToId = manager.Id });
        var advisor = await PostAsync<AgentDto>(ownerClient, "/api/agents", new SaveAgentRequest { Name = "小周", Title = "顾问" });

        var chart = await GetAsync<List<OrgChartNodeDto>>(ownerClient, "/api/agents/org-chart");

        chart.Select(n => n.Agent.Id).Should().BeEquivalentTo([chief.Id, advisor.Id]);
        var chiefNode = chart.Single(n => n.Agent.Id == chief.Id);
        var managerNode = chiefNode.Reports.Should().ContainSingle().Subject;
        managerNode.Agent.Id.Should().Be(manager.Id);
        managerNode.Reports.Should().ContainSingle().Which.Agent.Id.Should().Be(analyst.Id);
        chart.Single(n => n.Agent.Id == advisor.Id).Reports.Should().BeEmpty();

        // 终止经理后，下属改为向经理的上级汇报，组织图里不再出现被终止的智能体。
        (await PostAsync<AgentDto>(ownerClient, $"/api/agents/{manager.Id}/terminate", new { })).Status.Should().Be(AgentStatus.Terminated);
        var viewer = (await GetAsync<List<RoleDto>>(ownerClient, "/api/roles")).Single(r => r.Key == "viewer");
        using var viewerClient = Client(await AddMemberAsync(ownerClient, "viewer@example.com", viewer.Id));
        var after = await GetAsync<List<OrgChartNodeDto>>(viewerClient, "/api/agents/org-chart");

        Flatten(after).Select(n => n.Agent.Id).Should().BeEquivalentTo([chief.Id, analyst.Id, advisor.Id]);
        after.Single(n => n.Agent.Id == chief.Id).Reports.Should().ContainSingle().Which.Agent.Id.Should().Be(analyst.Id);
        (await GetAsync<AgentDto>(ownerClient, $"/api/agents/{analyst.Id}")).ReportsToId.Should().Be(chief.Id);
    }

    private static Task<HttpResponseMessage> CreateTaskAsync(HttpClient client, string title)
        => client.PostAsJsonAsync("/api/work-items", new CreateWorkItemRequest { Title = title }, PlatformJson.Options, Ct);

    private static IEnumerable<OrgChartNodeDto> Flatten(IEnumerable<OrgChartNodeDto> nodes)
        => nodes.SelectMany(n => Flatten(n.Reports).Prepend(n));
}
