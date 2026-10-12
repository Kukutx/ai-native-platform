using System.Net;
using System.Net.Http.Json;
using AiNative.Application.Common;
using AiNative.Application.Tenancy;
using AiNative.Application.Work;
using AiNative.Contracts.Actions;
using AiNative.Contracts.Agents;
using AiNative.Contracts.Platform;
using AiNative.Contracts.Projects;
using AiNative.Contracts.Work;
using AiNative.Domain.Agents;
using AiNative.Domain.Common;
using AiNative.Domain.Work;
using AiNative.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiNative.IntegrationTests;

public sealed class ProjectScheduleTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Now);

    private static DateOnly Day(int offset) => Today.AddDays(offset);

    [Fact]
    public async Task Dependencies_reject_self_duplicates_and_cycles_and_show_on_both_ends()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var design = await CreateAsync(client, "设计");
        var build = await CreateAsync(client, "开发");
        var test = await CreateAsync(client, "测试");

        var link = await PostAsync<WorkItemLinkDto>(client, $"/api/work-items/{build.Id}/dependencies", new AddWorkItemDependencyRequest { DependsOnId = design.Id });
        link.Should().BeEquivalentTo(new { design.Id, design.Key, Title = "设计", Status = WorkItemStatus.Todo });
        await PostAsync<WorkItemLinkDto>(client, $"/api/work-items/{test.Id}/dependencies", new AddWorkItemDependencyRequest { DependsOnId = build.Id });

        (await AddDependencyAsync(client, design.Id, design.Id)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CodeAsync(await AddDependencyAsync(client, build.Id, design.Id))).Should().Be("work_item_dependency.duplicate");

        // 直接和间接的循环都被拒绝。
        var direct = await AddDependencyAsync(client, design.Id, build.Id);
        direct.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(direct)).Should().Be("work_item_dependency.cycle");
        (await CodeAsync(await AddDependencyAsync(client, design.Id, test.Id))).Should().Be("work_item_dependency.cycle");

        var detail = await GetAsync<WorkItemDetailDto>(client, $"/api/work-items/{build.Id}");
        detail.Dependencies.Should().ContainSingle(d => d.Id == design.Id);
        detail.Dependents.Should().ContainSingle(d => d.Id == test.Id && d.Key == test.Key);

        // 新建时一并指定前置任务；最多 50 个。
        var release = await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "发布", DependsOn = [test.Id, build.Id, test.Id] });
        (await GetAsync<WorkItemDetailDto>(client, $"/api/work-items/{release.Id}")).Dependencies.Select(d => d.Title).Should().Equal("开发", "测试");
        var tooMany = await client.PostAsJsonAsync(
            "/api/work-items", new CreateWorkItemRequest { Title = "x", DependsOn = [.. Enumerable.Range(0, 51).Select(_ => Guid.CreateVersion7())] }, PlatformJson.Options, Ct);
        tooMany.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var missing = await client.PostAsJsonAsync("/api/work-items", new CreateWorkItemRequest { Title = "x", DependsOn = [Guid.CreateVersion7()] }, PlatformJson.Options, Ct);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 去掉依赖；不存在的依赖返回 404。
        var removed = await client.DeleteAsync(new Uri($"/api/work-items/{test.Id}/dependencies/{build.Id}", UriKind.Relative), Ct);
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.DeleteAsync(new Uri($"/api/work-items/{test.Id}/dependencies/{build.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AddDependencyAsync(client, design.Id, test.Id)).StatusCode.Should().Be(HttpStatusCode.OK);

        // 只读角色能看不能改；别的空间看不到这些任务，也不能依赖它们。
        var viewer = (await GetAsync<List<RoleDto>>(client, "/api/roles")).Single(r => r.Key == "viewer");
        using var viewerClient = Client(await AddMemberAsync(client, "viewer@example.com", viewer.Id));
        (await GetAsync<WorkItemDetailDto>(viewerClient, $"/api/work-items/{build.Id}")).Dependencies.Should().ContainSingle();
        (await AddDependencyAsync(viewerClient, release.Id, design.Id)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var stranger = await RegisterAsync("other@example.com", "别的空间");
        using var strangerClient = Client(stranger.AccessToken);
        var own = await CreateAsync(strangerClient, "自己的任务");
        (await AddDependencyAsync(strangerClient, own.Id, design.Id)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var foreign = await strangerClient.PostAsJsonAsync("/api/work-items", new CreateWorkItemRequest { Title = "x", DependsOn = [design.Id] }, PlatformJson.Options, Ct);
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Concurrent_opposite_dependencies_never_form_a_cycle()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        for (var round = 0; round < 6; round++)
        {
            var first = await CreateAsync(client, $"甲 {round}");
            var second = await CreateAsync(client, $"乙 {round}");
            var responses = await Task.WhenAll(AddDependencyAsync(client, first.Id, second.Id), AddDependencyAsync(client, second.Id, first.Id));

            // 依赖锁让后一个看到前一个：只有一条成功，另一条因为循环被拒绝。
            responses.Count(r => r.IsSuccessStatusCode).Should().Be(1, $"第 {round} 轮");
            (await CodeAsync(responses.Single(r => !r.IsSuccessStatusCode))).Should().Be("work_item_dependency.cycle");
        }
    }

    [Fact]
    public async Task Agents_wait_for_predecessors_and_are_woken_when_they_close()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var agent = await StarterAgentAsync(client);
        await PostAsync<AgentDto>(client, $"/api/agents/{agent.Id}/pause", new { });
        var spec = await CreateAsync(client, "写需求");
        var review = await CreateAsync(client, "评审");
        var build = await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "实现", AssigneeAgentId = agent.Id, DependsOn = [spec.Id, review.Id] });

        // 前置任务没关闭：指派不唤醒，也领取不到。
        (await WakeItemsAsync(agent.Id)).Should().NotContain(i => i.WorkItemId == build.Id);
        (await TryCheckoutAsync(auth.Session.Tenant.Id, build.Id, agent.Id)).Should().BeFalse();

        // 关掉一个还剩一个，仍在等；最后一个取消后唤醒负责人，聚焦到后续任务。
        await PostStatusAsync(client, spec, WorkItemStatus.Done);
        (await WakeItemsAsync(agent.Id)).Should().NotContain(i => i.WorkItemId == build.Id);
        await PostStatusAsync(client, review, WorkItemStatus.Cancelled);
        (await WakeItemsAsync(agent.Id)).Should().ContainSingle(i => i.WorkItemId == build.Id && i.Source == WakeupSource.Dependency)
            .Which.Reason.Should().Contain($"前置任务 {review.Key}《评审》已取消").And.Contain($"{build.Key}《实现》可以开始了");
        (await TryCheckoutAsync(auth.Session.Tenant.Id, build.Id, agent.Id)).Should().BeTrue();

        // 去掉最后一个未关闭的前置任务同样会唤醒；去掉已关闭的前置任务不会。
        var deploy = await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "部署", AssigneeAgentId = agent.Id, DependsOn = [build.Id, spec.Id] });
        (await client.DeleteAsync(new Uri($"/api/work-items/{deploy.Id}/dependencies/{spec.Id}", UriKind.Relative), Ct)).EnsureSuccessStatusCode();
        (await WakeItemsAsync(agent.Id)).Should().NotContain(i => i.WorkItemId == deploy.Id);
        (await client.DeleteAsync(new Uri($"/api/work-items/{deploy.Id}/dependencies/{build.Id}", UriKind.Relative), Ct)).EnsureSuccessStatusCode();
        (await WakeItemsAsync(agent.Id)).Should().ContainSingle(i => i.WorkItemId == deploy.Id)
            .Which.Reason.Should().Contain($"不再等待前置任务 {build.Key}");

        // 智能体的提示里标出还在等的前置任务。
        var waiting = await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "验收", AssigneeAgentId = agent.Id, DependsOn = [deploy.Id] });
        var prompt = await PromptAsync(auth.Session.Tenant.Id, agent.Id);
        prompt.Should().Contain($"{waiting.Key}《验收》").And.Contain($"等待前置任务 {deploy.Key} 关闭后才能开始");
    }

    [Fact]
    public async Task Rescheduling_shifts_dependents_and_the_schedule_reports_conflicts_and_the_critical_path()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var project = (await PostAsync<ProjectDetailDto>(client, "/api/projects", new SaveProjectRequest { Name = "新版官网", StartDate = Day(0) })).Project;

        // A(1–3) → B(4–6) → C(7–8)，A → D(4–10)。
        var a = await CreateAsync(client, "A 调研", project.Id, Day(1), Day(3));
        var b = await CreateAsync(client, "B 设计", project.Id, Day(4), Day(6), a.Id);
        var c = await CreateAsync(client, "C 评审", project.Id, Day(7), Day(8), b.Id);
        var d = await CreateAsync(client, "D 开发", project.Id, Day(4), Day(10), a.Id);
        await CreateAsync(client, "E 待定", project.Id);
        var dropped = await CreateAsync(client, "F 放弃", project.Id, Day(2), Day(2));
        await PostStatusAsync(client, dropped, WorkItemStatus.Cancelled);

        var schedule = await ScheduleAsync(client, project.Id);
        schedule.Tasks.Select(t => t.Title).Should().BeEquivalentTo(["A 调研", "B 设计", "C 评审", "D 开发", "E 待定"]);
        schedule.Links.Should().HaveCount(3).And.OnlyContain(l => !l.Conflict);
        (schedule.Start, schedule.End, schedule.Unscheduled).Should().Be((Day(0), Day(10), 1));
        Critical(schedule).Should().BeEquivalentTo(["D 开发"]);
        schedule.Tasks.Single(t => t.Id == b.Id).WaitingOn.Should().Be(1);

        // 只改日期不顺延：B、D 开始得比 A 的截止早，记为冲突；A 也因此进入关键路径。
        var moved = await PutAsync<ScheduleWorkItemResultDto>(client, $"/api/work-items/{a.Id}/schedule", new ScheduleWorkItemRequest
        {
            StartDate = Day(1),
            DueDate = Day(5),
            ExpectedUpdatedAt = a.UpdatedAt,
        });
        moved.Shifted.Should().BeEmpty();
        schedule = await ScheduleAsync(client, project.Id);
        schedule.Conflicts.Should().Be(2);
        schedule.Links.Where(l => l.Conflict).Select(l => l.ToId).Should().BeEquivalentTo([b.Id, d.Id]);
        Critical(schedule).Should().BeEquivalentTo(["A 调研", "D 开发"]);

        // 基于旧版本修改返回冲突；开始晚于截止被拒绝。
        var stale = await client.PutAsJsonAsync($"/api/work-items/{a.Id}/schedule", new ScheduleWorkItemRequest { DueDate = Day(6), ExpectedUpdatedAt = a.UpdatedAt }, PlatformJson.Options, Ct);
        (await CodeAsync(stale)).Should().Be("work_item.modified");
        var inverted = await client.PutAsJsonAsync($"/api/work-items/{a.Id}/schedule", new ScheduleWorkItemRequest { StartDate = Day(6), DueDate = Day(5) }, PlatformJson.Options, Ct);
        inverted.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 顺延：B、D 各挪一天；C 第 7 天开始不早于 B 的新截止第 7 天，不动。
        var shifted = await PutAsync<ScheduleWorkItemResultDto>(client, $"/api/work-items/{a.Id}/schedule", new ScheduleWorkItemRequest
        {
            StartDate = Day(1),
            DueDate = Day(5),
            ShiftDependents = true,
            ExpectedUpdatedAt = moved.Item.UpdatedAt,
        });
        shifted.Shifted.Should().BeEquivalentTo(new[]
        {
            new ShiftedWorkItemDto(b.Id, b.Key, "B 设计", Day(5), Day(7), 1),
            new ShiftedWorkItemDto(d.Id, d.Key, "D 开发", Day(5), Day(11), 1),
        });
        schedule = await ScheduleAsync(client, project.Id);
        schedule.Conflicts.Should().Be(0);
        schedule.End.Should().Be(Day(11));
        Critical(schedule).Should().BeEquivalentTo(["A 调研", "D 开发"]);
        var detail = await GetAsync<WorkItemDetailDto>(client, $"/api/work-items/{c.Id}");
        (detail.Item.StartDate, detail.Item.DueDate).Should().Be((Day(7), Day(8)));

        // 已关闭的后续任务不动；被顺延的任务也会推动它自己的后续任务。
        await PostStatusAsync(client, c, WorkItemStatus.Done);
        var late = await PutAsync<ScheduleWorkItemResultDto>(client, $"/api/work-items/{a.Id}/schedule", new ScheduleWorkItemRequest { StartDate = Day(1), DueDate = Day(8), ShiftDependents = true });
        late.Shifted.Select(s => (s.Key, s.Days)).Should().BeEquivalentTo([(b.Key, 3), (d.Key, 3)]);
        detail = await GetAsync<WorkItemDetailDto>(client, $"/api/work-items/{c.Id}");
        (detail.Item.StartDate, detail.Item.DueDate).Should().Be((Day(7), Day(8)));

        // 排期要能看任务：只有项目查看权限的角色被拒绝。
        var projectsOnly = await PostAsync<RoleDto>(client, "/api/roles", new SaveRoleRequest { Key = "pm-lite", Name = "只看项目", Features = ["projects.view"] });
        using var limited = Client(await AddMemberAsync(client, "lite@example.com", projectsOnly.Id));
        (await limited.GetAsync(new Uri($"/api/projects/{project.Id}/schedule", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Actions_link_tasks_and_shift_the_plan()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var planned = await InvokeAsync(client, "projects.create", new { name = "数据迁移" });
        var projectId = planned.GetProperty("project").GetProperty("id").GetGuid();
        var export = await InvokeAsync(client, "tasks.create", new { title = "导出旧数据", projectId, startDate = Day(1), dueDate = Day(2) });
        var exportId = export.GetProperty("id").GetGuid();
        export.GetProperty("startDate").GetString().Should().Be(Day(1).ToString("yyyy-MM-dd"));
        var import = await InvokeAsync(client, "tasks.create", new { title = "导入新库", projectId, startDate = Day(3), dueDate = Day(4), dependsOn = new[] { exportId } });
        var importId = import.GetProperty("id").GetGuid();
        var verify = await InvokeAsync(client, "tasks.create", new { title = "核对", projectId, startDate = Day(5), dueDate = Day(5) });
        var verifyId = verify.GetProperty("id").GetGuid();

        var linked = await InvokeAsync(client, "tasks.add_dependency", new { taskId = verifyId, dependsOnTaskId = importId });
        linked.GetProperty("title").GetString().Should().Be("导入新库");
        var cycle = await PostAsync<InvokeActionResponse>(client, "/api/actions/tasks.add_dependency/invoke", new { taskId = exportId, dependsOnTaskId = verifyId });
        cycle.Status.Should().NotBe("succeeded");
        cycle.Error.Should().Contain("循环");

        // 导出延期三天：导入挪到导出截止那天开始，核对再跟着导入挪。
        var result = await InvokeAsync(client, "tasks.schedule", new { taskId = exportId, dueDate = Day(5), shiftDependents = true });
        result.GetProperty("item").GetProperty("dueDate").GetString().Should().Be(Day(5).ToString("yyyy-MM-dd"));
        result.GetProperty("shifted").EnumerateArray().Select(s => (s.GetProperty("title").GetString(), s.GetProperty("days").GetInt32()))
            .Should().Equal(("导入新库", 2), ("核对", 1));

        var schedule = await InvokeAsync(client, "projects.get_schedule", new { project = "数据迁移" });
        schedule.GetProperty("conflicts").GetInt32().Should().Be(0);
        schedule.GetProperty("links").GetArrayLength().Should().Be(2);
        schedule.GetProperty("tasks").EnumerateArray().Count(t => t.GetProperty("critical").GetBoolean()).Should().Be(3);

        (await InvokeAsync(client, "tasks.get", new { taskId = verifyId })).GetProperty("dependencies")[0].GetProperty("id").GetGuid().Should().Be(importId);
        (await InvokeAsync(client, "tasks.remove_dependency", new { taskId = verifyId, dependsOnTaskId = importId })).GetProperty("id").GetGuid().Should().Be(importId);
        (await InvokeAsync(client, "tasks.get", new { taskId = verifyId })).GetProperty("dependencies").GetArrayLength().Should().Be(0);
    }

    private static Task<WorkItemDto> CreateAsync(
        HttpClient client,
        string title,
        Guid? projectId = null,
        DateOnly? start = null,
        DateOnly? due = null,
        Guid? dependsOn = null)
        => PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest
        {
            Title = title,
            ProjectId = projectId,
            StartDate = start,
            DueDate = due,
            DependsOn = dependsOn is { } id ? [id] : null,
        });

    private static Task<HttpResponseMessage> AddDependencyAsync(HttpClient client, Guid workItemId, Guid dependsOnId)
        => client.PostAsJsonAsync($"/api/work-items/{workItemId}/dependencies", new AddWorkItemDependencyRequest { DependsOnId = dependsOnId }, PlatformJson.Options, Ct);

    private static Task PostStatusAsync(HttpClient client, WorkItemDto item, WorkItemStatus status)
        => InvokeAsync(client, "tasks.update_status", new { taskId = item.Id, status });

    private static Task<ProjectScheduleDto> ScheduleAsync(HttpClient client, Guid projectId) => GetAsync<ProjectScheduleDto>(client, $"/api/projects/{projectId}/schedule");

    private static IEnumerable<string> Critical(ProjectScheduleDto schedule) => schedule.Tasks.Where(t => t.Critical).Select(t => t.Title);

    private async Task<List<WakeupItem>> WakeItemsAsync(Guid agentId)
        => (await WithDbAsync(db => db.WakeupRequests.IgnoreQueryFilters().AsNoTracking().Where(w => w.AgentId == agentId).ToListAsync(Ct)))
            .SelectMany(w => w.Items).ToList();

    private Task<bool> TryCheckoutAsync(Guid tenantId, Guid workItemId, Guid agentId)
        => Factory.Services.GetRequiredService<ITenantScopeFactory>().RunAsync(tenantId, Actor.System, [], sp =>
            sp.GetRequiredService<IWorkItemCheckout>().TryCheckoutAsync(workItemId, agentId, Guid.CreateVersion7(), Ct));

    private Task<string> PromptAsync(Guid tenantId, Guid agentId)
        => Factory.Services.GetRequiredService<ITenantScopeFactory>().RunAsync(tenantId, Actor.System, [], async sp =>
        {
            var agent = await sp.GetRequiredService<AiNative.Infrastructure.Persistence.AppDbContext>().Agents.SingleAsync(a => a.Id == agentId, Ct);
            return await sp.GetRequiredService<AiNative.Application.Agents.Runtime.AgentPromptBuilder>().BuildWakeMessageAsync(agent, null, null, false, Ct);
        });
}
