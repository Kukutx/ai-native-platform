using System.Net;
using System.Net.Http.Json;
using AiNative.Application.Common;
using AiNative.Contracts.Actions;
using AiNative.Contracts.Agents;
using AiNative.Contracts.Common;
using AiNative.Contracts.Platform;
using AiNative.Contracts.Projects;
using AiNative.Contracts.Work;
using AiNative.Contracts.Workflows;
using AiNative.Domain.Agents;
using AiNative.Domain.Projects;
using AiNative.Domain.Work;
using AiNative.Domain.Workflows;
using AiNative.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AiNative.IntegrationTests;

public sealed class ProjectsTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Now);

    [Fact]
    public async Task Progress_counts_tasks_per_milestone_and_subtasks_follow_their_parent()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var created = await PostAsync<ProjectDetailDto>(client, "/api/projects", new SaveProjectRequest
        {
            Name = "官网改版",
            LeadUserId = auth.Session.User.Id,
            TargetDate = Today.AddDays(30),
        });
        var project = created.Project;
        project.Lead!.Id.Should().Be(auth.Session.User.Id);
        project.MemberCount.Should().Be(1);
        created.Members.Should().ContainSingle(m => m.Member.Id == auth.Session.User.Id && m.Responsibility.Length == 0);
        var design = await PostAsync<ProjectMilestoneDto>(client, $"/api/projects/{project.Id}/milestones", new SaveProjectMilestoneRequest { Name = "设计稿", DueDate = Today.AddDays(7) });
        var launch = await PostAsync<ProjectMilestoneDto>(client, $"/api/projects/{project.Id}/milestones", new SaveProjectMilestoneRequest { Name = "上线" });

        // 只给里程碑时项目取里程碑所在的项目；子任务沿用上级任务的项目和里程碑。
        var wireframe = await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "线框图", MilestoneId = design.Id, DueDate = Today.AddDays(-1) });
        wireframe.ProjectId.Should().Be(project.Id);
        wireframe.MilestoneId.Should().Be(design.Id);
        var sub = await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "首页线框", ParentId = wireframe.Id, Status = WorkItemStatus.Done });
        (sub.ProjectId, sub.MilestoneId).Should().Be((project.Id, design.Id));
        await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "配色", MilestoneId = design.Id, Status = WorkItemStatus.Blocked });
        await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "部署", MilestoneId = launch.Id, Status = WorkItemStatus.InProgress });
        await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "放弃的想法", ProjectId = project.Id, Status = WorkItemStatus.Cancelled });
        await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "无关任务" });

        var detail = await GetAsync<ProjectDetailDto>(client, $"/api/projects/{project.Id}");
        detail.Project.Progress.Should().Be(new ProjectProgressDto(Total: 4, Done: 1, InProgress: 1, Blocked: 1, Overdue: 1, Percent: 25));
        detail.Project.MilestoneCount.Should().Be(2);
        detail.Milestones.Select(m => m.Name).Should().Equal("设计稿", "上线");
        detail.Milestones[0].Progress.Should().Be(new ProjectProgressDto(3, 1, 0, 1, 1, 33));
        detail.Milestones[1].Progress.Total.Should().Be(1);
        detail.Members.Single().OpenTasks.Should().Be(0);

        (await GetAsync<PagedResult<WorkItemDto>>(client, $"/api/work-items?projectId={project.Id}")).Total.Should().Be(5);
        (await GetAsync<PagedResult<WorkItemDto>>(client, $"/api/work-items?milestoneId={launch.Id}")).Items.Should().ContainSingle(t => t.Title == "部署");
        var listed = await GetAsync<PagedResult<ProjectDto>>(client, "/api/projects?search=官网");
        listed.Items.Should().ContainSingle().Which.Progress.Percent.Should().Be(25);
    }

    [Fact]
    public async Task Tasks_only_join_open_projects_and_unfinished_milestones()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var project = (await PostAsync<ProjectDetailDto>(client, "/api/projects", new SaveProjectRequest { Name = "发布 2.0" })).Project;
        var other = (await PostAsync<ProjectDetailDto>(client, "/api/projects", new SaveProjectRequest { Name = "内部工具" })).Project;
        var beta = await PostAsync<ProjectMilestoneDto>(client, $"/api/projects/{project.Id}/milestones", new SaveProjectMilestoneRequest { Name = "公测" });
        var task = await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "回归测试", MilestoneId = beta.Id });

        // 里程碑不属于所选项目。
        var mismatch = await client.PostAsJsonAsync("/api/work-items", new CreateWorkItemRequest { Title = "x", ProjectId = other.Id, MilestoneId = beta.Id }, PlatformJson.Options, Ct);
        mismatch.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 还有未关闭的任务时，里程碑和项目都不能标记完成。
        var early = await client.PostAsync(new Uri($"/api/projects/{project.Id}/milestones/{beta.Id}/complete", UriKind.Relative), null, Ct);
        early.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(early)).Should().Be("project_milestone.open_tasks");
        var earlyProject = await client.PutAsJsonAsync($"/api/projects/{project.Id}", Save(project) with { Status = ProjectStatus.Completed }, PlatformJson.Options, Ct);
        (await CodeAsync(earlyProject)).Should().Be("project.open_tasks");

        var done = await PutAsync<WorkItemDto>(client, $"/api/work-items/{task.Id}", Edit(task) with { Status = WorkItemStatus.Done });
        var completed = await PostAsync<ProjectMilestoneDto>(client, $"/api/projects/{project.Id}/milestones/{beta.Id}/complete", new { });
        completed.CompletedAt.Should().NotBeNull();
        var late = await client.PostAsJsonAsync("/api/work-items", new CreateWorkItemRequest { Title = "补测", MilestoneId = beta.Id }, PlatformJson.Options, Ct);
        late.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(late)).Should().Be("project_milestone.completed");

        // 编辑已完成任务的其他字段不改归属、也不改写完成时间。
        var completedAt = (await GetAsync<WorkItemDetailDto>(client, $"/api/work-items/{task.Id}")).Item.CompletedAt;
        var renamed = await PutAsync<WorkItemDto>(client, $"/api/work-items/{task.Id}", Edit(done) with { Title = "回归测试（全量）" });
        renamed.MilestoneId.Should().Be(beta.Id);
        renamed.CompletedAt.Should().Be(completedAt);

        var closed = await PutAsync<ProjectDto>(client, $"/api/projects/{project.Id}", Save(project) with { Status = ProjectStatus.Completed });
        closed.ClosedAt.Should().NotBeNull();
        var intoClosed = await client.PostAsJsonAsync("/api/work-items", new CreateWorkItemRequest { Title = "新需求", ProjectId = project.Id }, PlatformJson.Options, Ct);
        (await CodeAsync(intoClosed)).Should().Be("project.closed");
        var milestoneInClosed = await client.PostAsJsonAsync($"/api/projects/{project.Id}/milestones", new SaveProjectMilestoneRequest { Name = "二期" }, PlatformJson.Options, Ct);
        (await CodeAsync(milestoneInClosed)).Should().Be("project.closed");

        // 移出项目总是允许；删除项目时任务保留下来，只是不再属于任何项目。
        var moved = await PutAsync<WorkItemDto>(client, $"/api/work-items/{task.Id}", Edit(renamed) with { ProjectId = null, MilestoneId = null });
        (moved.ProjectId, moved.MilestoneId).Should().Be((null, null));
        var kept = await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "工具文档", ProjectId = other.Id });
        (await client.DeleteAsync(new Uri($"/api/projects/{other.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GetAsync<WorkItemDetailDto>(client, $"/api/work-items/{kept.Id}")).Item.ProjectId.Should().BeNull();
    }

    [Fact]
    public async Task Completing_a_milestone_never_interleaves_with_adding_a_task()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var project = (await PostAsync<ProjectDetailDto>(client, "/api/projects", new SaveProjectRequest { Name = "并发" })).Project;
        for (var round = 0; round < 6; round++)
        {
            var milestone = await PostAsync<ProjectMilestoneDto>(client, $"/api/projects/{project.Id}/milestones", new SaveProjectMilestoneRequest { Name = $"第 {round} 轮" });
            var complete = client.PostAsync(new Uri($"/api/projects/{project.Id}/milestones/{milestone.Id}/complete", UriKind.Relative), null, Ct);
            var create = client.PostAsJsonAsync("/api/work-items", new CreateWorkItemRequest { Title = $"任务 {round}", MilestoneId = milestone.Id }, PlatformJson.Options, Ct);
            var responses = await Task.WhenAll(complete, create);

            // 要么先完成、新任务被拒绝，要么先加任务、完成被拒绝，不会出现"已完成的里程碑里有未关闭的任务"。
            responses.Count(r => r.IsSuccessStatusCode).Should().Be(1, $"第 {round} 轮");
            responses.Single(r => !r.IsSuccessStatusCode).StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
    }

    [Fact]
    public async Task Permissions_names_and_tenants_are_enforced()
    {
        var owner = await RegisterAsync();
        using var ownerClient = Client(owner.AccessToken);
        var project = (await PostAsync<ProjectDetailDto>(ownerClient, "/api/projects", new SaveProjectRequest { Name = "季度规划" })).Project;
        var duplicate = await ownerClient.PostAsJsonAsync("/api/projects", new SaveProjectRequest { Name = "季度规划".ToUpperInvariant() + " " }, PlatformJson.Options, Ct);
        (await CodeAsync(duplicate)).Should().Be("project.duplicate_name");

        // 只读角色能看不能改。
        var viewer = (await GetAsync<List<RoleDto>>(ownerClient, "/api/roles")).Single(r => r.Key == "viewer");
        using var viewerClient = Client(await AddMemberAsync(ownerClient, "viewer@example.com", viewer.Id));
        (await GetAsync<PagedResult<ProjectDto>>(viewerClient, "/api/projects")).Items.Should().ContainSingle();
        var forbidden = await viewerClient.PostAsJsonAsync("/api/projects", new SaveProjectRequest { Name = "偷建" }, PlatformJson.Options, Ct);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 别的空间既看不到，也不能把任务放进来。
        var stranger = await RegisterAsync("other@example.com", "别的空间");
        using var strangerClient = Client(stranger.AccessToken);
        (await strangerClient.GetAsync(new Uri($"/api/projects/{project.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var foreign = await strangerClient.PostAsJsonAsync("/api/work-items", new CreateWorkItemRequest { Title = "x", ProjectId = project.Id }, PlatformJson.Options, Ct);
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync<PagedResult<ProjectDto>>(strangerClient, "/api/projects")).Items.Should().BeEmpty();

        // 模块停用后接口关闭，也不能再往项目里放任务；原有任务照常编辑。
        var task = await PostAsync<WorkItemDto>(ownerClient, "/api/work-items", new CreateWorkItemRequest { Title = "复盘", ProjectId = project.Id });
        await PutAsync<ModuleDto>(ownerClient, "/api/modules/projects", new SetModuleEnabledRequest { Enabled = false });
        (await ownerClient.GetAsync(new Uri("/api/projects", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var placed = await ownerClient.PostAsJsonAsync("/api/work-items", new CreateWorkItemRequest { Title = "y", ProjectId = project.Id }, PlatformJson.Options, Ct);
        placed.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PutAsync<WorkItemDto>(ownerClient, $"/api/work-items/{task.Id}", Edit(task) with { Title = "季度复盘" })).ProjectId.Should().Be(project.Id);
    }

    [Fact]
    public async Task Agent_lead_is_woken_when_assigned_blocked_and_when_a_milestone_finishes()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var agent = await StarterAgentAsync(client);
        await PostAsync<AgentDto>(client, $"/api/agents/{agent.Id}/pause", new { });
        var project = (await PostAsync<ProjectDetailDto>(client, "/api/projects", new SaveProjectRequest { Name = "客户回访", LeadAgentId = agent.Id })).Project;
        project.Lead!.Kind.Should().Be(Domain.Common.ActorKind.Agent);
        var milestone = await PostAsync<ProjectMilestoneDto>(client, $"/api/projects/{project.Id}/milestones", new SaveProjectMilestoneRequest { Name = "第一轮" });
        var call = await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "电话回访", MilestoneId = milestone.Id });
        var blocked = await PutAsync<WorkItemDto>(client, $"/api/work-items/{call.Id}", Edit(call) with { Status = WorkItemStatus.Blocked });
        await PutAsync<WorkItemDto>(client, $"/api/work-items/{call.Id}", Edit(blocked) with { Status = WorkItemStatus.Done });

        var items = (await WithDbAsync(db => db.WakeupRequests.IgnoreQueryFilters().AsNoTracking().Where(w => w.AgentId == agent.Id).ToListAsync(Ct)))
            .SelectMany(w => w.Items).ToList();
        items.Should().Contain(i => i.Source == WakeupSource.Assignment && i.Reason.Contains("项目《客户回访》的负责人"));
        items.Should().Contain(i => i.Source == WakeupSource.Delegation && i.Reason.Contains("被阻塞") && i.WorkItemId == call.Id);
        items.Should().Contain(i => i.Source == WakeupSource.Delegation && i.Reason.Contains("里程碑「第一轮」下的任务都已关闭"));
        (await GetAsync<ProjectDetailDto>(client, $"/api/projects/{project.Id}")).Members.Should().ContainSingle(m => m.Member.Id == agent.Id);

        // 负责人自己不能直接移出项目。
        var leadMember = (await GetAsync<ProjectDetailDto>(client, $"/api/projects/{project.Id}")).Members.Single();
        var removeLead = await client.DeleteAsync(new Uri($"/api/projects/{project.Id}/members/{leadMember.Id}", UriKind.Relative), Ct);
        (await CodeAsync(removeLead)).Should().Be("project_member.is_lead");
    }

    [Fact]
    public async Task Actions_plan_projects_and_schedule_tasks_by_name()
    {
        var owner = await RegisterAsync();
        using var ownerClient = Client(owner.AccessToken);
        var planned = await InvokeAsync(ownerClient, "projects.create", new
        {
            name = "年会筹备",
            targetDate = Today.AddDays(20),
            milestones = new object[] { new { name = "场地", dueDate = Today.AddDays(5) }, new { name = "节目" } },
        });
        planned.GetProperty("project").GetProperty("lead").GetProperty("id").GetGuid().Should().Be(owner.Session.User.Id);
        planned.GetProperty("project").GetProperty("milestoneCount").GetInt32().Should().Be(2);
        var projectId = planned.GetProperty("project").GetProperty("id").GetGuid();
        var venueId = planned.GetProperty("milestones")[0].GetProperty("id").GetGuid();

        var task = await InvokeAsync(ownerClient, "tasks.create", new { title = "订场地", milestoneId = venueId, dueDate = Today.AddDays(3) });
        task.GetProperty("projectId").GetGuid().Should().Be(projectId);

        // 只有任务权限、没有项目权限的角色也能用 tasks.schedule 调整截止日期。
        var planner = await PostAsync<RoleDto>(ownerClient, "/api/roles", new SaveRoleRequest { Key = "planner", Name = "排期", Features = ["tasks.manage"] });
        using var plannerClient = Client(await AddMemberAsync(ownerClient, "planner@example.com", planner.Id));
        var taskId = task.GetProperty("id").GetGuid();
        var rescheduled = await InvokeAsync(plannerClient, "tasks.schedule", new { taskId, dueDate = Today.AddDays(4) });
        rescheduled.GetProperty("dueDate").GetString().Should().Be(Today.AddDays(4).ToString("yyyy-MM-dd"));
        rescheduled.GetProperty("milestoneId").GetGuid().Should().Be(venueId);

        var listed = await InvokeAsync(ownerClient, "tasks.list", new { projectId });
        listed.GetArrayLength().Should().Be(1);
        var early = await PostAsync<InvokeActionResponse>(ownerClient, "/api/actions/projects.complete_milestone/invoke", new { project = "年会筹备", milestone = "场地" });
        early.Status.Should().NotBe("succeeded");
        early.Error.Should().Contain("未关闭");
        await InvokeAsync(ownerClient, "tasks.update_status", new { taskId, status = "done" });
        (await InvokeAsync(ownerClient, "projects.complete_milestone", new { project = "年会筹备", milestone = "场地" })).GetProperty("completedAt").ValueKind
            .Should().NotBe(System.Text.Json.JsonValueKind.Null);
        (await InvokeAsync(ownerClient, "projects.update", new { project = projectId.ToString(), status = "on_hold" })).GetProperty("status").GetString().Should().Be("on_hold");
        (await InvokeAsync(ownerClient, "projects.get", new { project = "年会筹备" })).GetProperty("milestones").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Workflow_step_creates_a_project_with_milestones_in_one_transaction()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var workflow = await PostAsync<WorkflowDto>(client, "/api/workflows", new SaveWorkflowRequest
        {
            Name = "新客户启动项目",
            Trigger = new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Manual },
            Steps =
            [
                new WorkflowStep
                {
                    Key = "project",
                    Name = "建项目",
                    Kind = WorkflowStepKind.Action,
                    ActionKey = "projects.create",
                    Input = Json("""{ "name": "{{ input.customer }} 实施", "milestones": [{ "name": "需求调研" }, { "name": "上线验收" }] }"""),
                },
                new WorkflowStep
                {
                    Key = "kickoff",
                    Name = "建启动任务",
                    Kind = WorkflowStepKind.Action,
                    ActionKey = "tasks.create",
                    Input = Json("""{ "title": "启动会", "milestoneId": "{{ steps.project.output.milestones[0].id }}" }"""),
                },
            ],
        });

        var started = await PostAsync<WorkflowRunSummaryDto>(client, $"/api/workflows/{workflow.Id}/runs", new StartWorkflowRunRequest { Input = Json("""{ "customer": "星火科技" }""") });
        var run = await EventuallyAsync(
            () => GetAsync<WorkflowRunDto>(client, $"/api/workflows/runs/{started.Id}"),
            r => r.Summary.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed);
        run.Summary.Status.Should().Be(WorkflowRunStatus.Succeeded, PlatformJson.Serialize(run));

        var project = (await GetAsync<PagedResult<ProjectDto>>(client, "/api/projects")).Items.Should().ContainSingle().Subject;
        project.Name.Should().Be("星火科技 实施");
        project.MilestoneCount.Should().Be(2);
        project.Progress.Total.Should().Be(1);
    }

    private static SaveProjectRequest Save(ProjectDto project) => new()
    {
        Name = project.Name,
        Description = project.Description,
        Status = project.Status,
        LeadUserId = project.Lead?.Kind == Domain.Common.ActorKind.User ? project.Lead.Id : null,
        LeadAgentId = project.Lead?.Kind == Domain.Common.ActorKind.Agent ? project.Lead.Id : null,
        StartDate = project.StartDate,
        TargetDate = project.TargetDate,
    };

    private static UpdateWorkItemRequest Edit(WorkItemDto item) => new()
    {
        Title = item.Title,
        Description = item.Description,
        Status = item.Status,
        Priority = item.Priority,
        ProjectId = item.ProjectId,
        MilestoneId = item.MilestoneId,
        DueDate = item.DueDate,
        AssigneeAgentId = item.AssigneeAgentId,
        AssigneeUserId = item.AssigneeUserId,
        ExpectedUpdatedAt = item.UpdatedAt,
    };
}
