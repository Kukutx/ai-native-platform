using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiNative.Application.Common;
using AiNative.Contracts.Actions;
using AiNative.Contracts.Auth;
using AiNative.Contracts.Common;
using AiNative.Contracts.Crm;
using AiNative.Contracts.Platform;
using AiNative.Contracts.Work;
using AiNative.Contracts.Workflows;
using AiNative.Domain.Crm;
using AiNative.Domain.Workflows;
using AiNative.IntegrationTests.Infrastructure;

namespace AiNative.IntegrationTests;

public sealed class CrmTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task New_tenant_comes_with_default_stages()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);

        var stages = await GetAsync<List<CrmStageDto>>(client, "/api/crm/stages");

        stages.Select(s => s.Name).Should().Equal("线索", "需求确认", "方案报价", "商务谈判", "赢单", "输单");
        stages.Select(s => s.Position).Should().Equal(0, 1, 2, 3, 4, 5);
        stages[4].Kind.Should().Be(CrmStageKind.Won);
        stages[5].Kind.Should().Be(CrmStageKind.Lost);
        auth.Session.Features.Should().Contain("*");
    }

    [Fact]
    public async Task Deals_move_through_the_pipeline_with_activities_and_totals()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var stages = await StagesAsync(client);
        var company = await PostAsync<CrmCompanyDto>(client, "/api/crm/companies", new SaveCrmCompanyRequest
        {
            Name = "星火科技",
            Industry = "软件",
            Tags = ["重点客户", " 华东 ", "重点客户", ""],
        });
        var contact = await PostAsync<CrmContactDto>(client, "/api/crm/contacts", new SaveCrmContactRequest
        {
            Name = "王经理",
            CompanyId = company.Id,
            Title = "采购总监",
            Email = "wang@example.com",
        });

        company.Tags.Should().Equal("重点客户", "华东");
        company.Owner!.Id.Should().Be(auth.Session.User.Id);
        contact.CompanyName.Should().Be("星火科技");

        // 只选联系人时自动带上其所属客户；不指定阶段时放进第一个进行中的阶段。
        var deal = await PostAsync<CrmDealDto>(client, "/api/crm/deals", new SaveCrmDealRequest { Title = "年度采购", ContactId = contact.Id, Amount = 100000 });
        var usdDeal = await PostAsync<CrmDealDto>(client, "/api/crm/deals", new SaveCrmDealRequest
        {
            Title = "海外子公司",
            CompanyId = company.Id,
            StageId = stages["方案报价"].Id,
            Amount = 50000,
            Currency = "USD",
        });
        deal.CompanyId.Should().Be(company.Id);
        deal.StageName.Should().Be("线索");
        deal.Status.Should().Be(CrmDealStatus.Open);
        deal.Probability.Should().Be(10);
        usdDeal.Probability.Should().Be(50);

        var moved = await PostAsync<CrmDealDto>(client, $"/api/crm/deals/{deal.Id}/stage", new MoveCrmDealRequest { StageId = stages["商务谈判"].Id, Note = "价格谈妥" });
        await PostAsync<CrmActivityDto>(client, "/api/crm/activities", new LogCrmActivityRequest { Kind = CrmActivityKind.Call, Content = "确认了交付时间", ContactId = contact.Id });

        moved.StageName.Should().Be("商务谈判");
        moved.Probability.Should().Be(75);
        moved.LastActivityAt.Should().NotBeNull();
        var detail = await GetAsync<CrmCompanyDetailDto>(client, $"/api/crm/companies/{company.Id}");
        detail.Company.ContactCount.Should().Be(1);
        detail.Company.OpenDealCount.Should().Be(2);
        detail.Company.LastActivityAt.Should().NotBeNull();
        detail.Contacts.Should().ContainSingle(c => c.Id == contact.Id);
        detail.Deals.Should().HaveCount(2);
        detail.Activities.Select(a => (a.Kind, a.Content)).Should().Equal(
            (CrmActivityKind.Call, "确认了交付时间"),
            (CrmActivityKind.StageChange, "「线索」→「商务谈判」：价格谈妥"));
        detail.Activities[0].CompanyName.Should().Be("星火科技");
        detail.Activities[1].DealTitle.Should().Be("年度采购");
        detail.Activities[1].Author.Id.Should().Be(auth.Session.User.Id);

        var pipeline = await GetAsync<CrmPipelineDto>(client, "/api/crm/deals/pipeline");
        pipeline.Stages.Should().HaveCount(6);
        pipeline.Stages.Single(s => s.Stage.Name == "商务谈判").Deals.Should().ContainSingle(d => d.Id == deal.Id);
        pipeline.OpenTotals.Should().BeEquivalentTo([new CrmAmountDto("CNY", 100000), new CrmAmountDto("USD", 50000)]);
        pipeline.WeightedTotals.Should().BeEquivalentTo([new CrmAmountDto("CNY", 75000), new CrmAmountDto("USD", 25000)]);
        pipeline.WonThisMonth.Should().BeEmpty();

        var won = await PostAsync<CrmDealDto>(client, $"/api/crm/deals/{deal.Id}/stage", new MoveCrmDealRequest { StageId = stages["赢单"].Id });
        won.Status.Should().Be(CrmDealStatus.Won);
        won.ClosedAt.Should().NotBeNull();
        pipeline = await GetAsync<CrmPipelineDto>(client, "/api/crm/deals/pipeline");
        pipeline.WonThisMonth.Should().BeEquivalentTo([new CrmAmountDto("CNY", 100000)]);
        pipeline.OpenTotals.Should().BeEquivalentTo([new CrmAmountDto("USD", 50000)]);
        pipeline.Stages.Single(s => s.Stage.Name == "赢单").DealCount.Should().Be(1);

        // 重新打开：状态回到进行中，结束时间清除。
        var reopened = await PostAsync<CrmDealDto>(client, $"/api/crm/deals/{deal.Id}/stage", new MoveCrmDealRequest { StageId = stages["商务谈判"].Id });
        reopened.Status.Should().Be(CrmDealStatus.Open);
        reopened.ClosedAt.Should().BeNull();
        var dealDetail = await GetAsync<CrmDealDetailDto>(client, $"/api/crm/deals/{deal.Id}");
        dealDetail.Activities.Count(a => a.Kind == CrmActivityKind.StageChange).Should().Be(3);

        var filtered = await GetAsync<PagedResult<CrmDealDto>>(client, $"/api/crm/deals?status=open&companyId={company.Id}");
        filtered.Items.Should().HaveCount(2);
        var contactDetail = await GetAsync<CrmContactDetailDto>(client, $"/api/crm/contacts/{contact.Id}");
        contactDetail.Deals.Should().ContainSingle(d => d.Id == deal.Id);
    }

    [Fact]
    public async Task Edits_need_the_version_that_was_read_and_moves_need_a_real_stage()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var company = await PostAsync<CrmCompanyDto>(client, "/api/crm/companies", new SaveCrmCompanyRequest { Name = "星火科技" });
        var updated = await PutAsync<CrmCompanyDto>(client, $"/api/crm/companies/{company.Id}", new SaveCrmCompanyRequest
        {
            Name = "星火科技有限公司",
            ExpectedUpdatedAt = company.UpdatedAt,
        });

        var stale = await client.PutAsJsonAsync($"/api/crm/companies/{company.Id}", new SaveCrmCompanyRequest
        {
            Name = "覆盖",
            ExpectedUpdatedAt = company.UpdatedAt,
        }, PlatformJson.Options, Ct);

        updated.Name.Should().Be("星火科技有限公司");
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(stale)).Should().Be("crm_company.modified");

        var deal = await PostAsync<CrmDealDto>(client, "/api/crm/deals", new SaveCrmDealRequest { Title = "年度采购", CompanyId = company.Id });
        var missingStage = await client.PostAsJsonAsync($"/api/crm/deals/{deal.Id}/stage", new MoveCrmDealRequest { StageId = Guid.NewGuid() }, PlatformJson.Options, Ct);
        missingStage.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 联系人与客户对不上时拒绝。
        var otherCompany = await PostAsync<CrmCompanyDto>(client, "/api/crm/companies", new SaveCrmCompanyRequest { Name = "别家" });
        var contact = await PostAsync<CrmContactDto>(client, "/api/crm/contacts", new SaveCrmContactRequest { Name = "李工", CompanyId = otherCompany.Id });
        var mismatch = await client.PostAsJsonAsync("/api/crm/deals", new SaveCrmDealRequest { Title = "错配", CompanyId = company.Id, ContactId = contact.Id }, PlatformJson.Options, Ct);
        mismatch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await mismatch.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").TryGetProperty("contactId", out _).Should().BeTrue();

        var invalid = await client.PostAsJsonAsync("/api/crm/deals", new SaveCrmDealRequest { Title = " ", Amount = -1, Currency = "人民币" }, PlatformJson.Options, Ct);
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");
        errors.TryGetProperty("title", out _).Should().BeTrue();
        errors.TryGetProperty("amount", out _).Should().BeTrue();
        errors.TryGetProperty("currency", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Stage_settings_protect_stages_in_use()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var stages = await StagesAsync(client);
        var deal = await PostAsync<CrmDealDto>(client, "/api/crm/deals", new SaveCrmDealRequest { Title = "年度采购", StageId = stages["方案报价"].Id });
        var current = stages.Values.OrderBy(s => s.Position).ToList();

        // 删除仍有商机的阶段被拒绝。
        var removeInUse = await client.PutAsJsonAsync("/api/crm/stages", Save(current.Where(s => s.Name != "方案报价")), PlatformJson.Options, Ct);
        removeInUse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(removeInUse)).Should().Be("crm_stage.in_use");

        var noOpen = await client.PutAsJsonAsync("/api/crm/stages", Save(current.Where(s => s.Kind != CrmStageKind.Open)), PlatformJson.Options, Ct);
        noOpen.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var duplicate = await client.PutAsJsonAsync("/api/crm/stages", new SaveCrmStagesRequest
        {
            Stages = [new SaveCrmStageItem { Name = "线索", Probability = 10 }, new SaveCrmStageItem { Name = "线索", Probability = 20 }],
        }, PlatformJson.Options, Ct);
        duplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 有商机的阶段不能直接改类型：状态、结束时间和事件都要随"推进"产生。
        var kindInUse = await client.PutAsJsonAsync(
            "/api/crm/stages", Save(current.Select(s => s.Id == stages["方案报价"].Id ? s with { Kind = CrmStageKind.Won } : s)), PlatformJson.Options, Ct);
        kindInUse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(kindInUse)).Should().Be("crm_stage.kind_in_use");
        (await GetAsync<CrmDealDetailDto>(client, $"/api/crm/deals/{deal.Id}")).Deal.Status.Should().Be(CrmDealStatus.Open);

        // 改名、删掉没有商机的阶段、新增阶段、调整顺序；没有商机的"商务谈判"可以改成赢单类型。
        var saved = await PutAsync<List<CrmStageDto>>(client, "/api/crm/stages", new SaveCrmStagesRequest
        {
            Stages =
            [
                new SaveCrmStageItem { Id = stages["线索"].Id, Name = "初步接触", Probability = 5, Kind = CrmStageKind.Open },
                new SaveCrmStageItem { Name = "试用中", Probability = 40, Kind = CrmStageKind.Open },
                new SaveCrmStageItem { Id = stages["方案报价"].Id, Name = "方案报价", Probability = 60, Kind = CrmStageKind.Open },
                new SaveCrmStageItem { Id = stages["商务谈判"].Id, Name = "签约", Probability = 100, Kind = CrmStageKind.Won },
                new SaveCrmStageItem { Id = stages["输单"].Id, Name = "输单", Probability = 0, Kind = CrmStageKind.Lost },
            ],
        });

        saved.Select(s => s.Name).Should().Equal("初步接触", "试用中", "方案报价", "签约", "输单");
        saved.Select(s => s.Position).Should().Equal(0, 1, 2, 3, 4);
        saved[0].Id.Should().Be(stages["线索"].Id);
        saved[3].Kind.Should().Be(CrmStageKind.Won);
        var unchanged = await GetAsync<CrmDealDetailDto>(client, $"/api/crm/deals/{deal.Id}");
        unchanged.Deal.Status.Should().Be(CrmDealStatus.Open);
        unchanged.Deal.StageName.Should().Be("方案报价");
    }

    [Fact]
    public async Task Roles_modules_and_tenants_limit_access()
    {
        var owner = await RegisterAsync();
        using var ownerClient = Client(owner.AccessToken);
        var roles = await GetAsync<List<RoleDto>>(ownerClient, "/api/roles");
        using var viewerClient = Client(await AddMemberAsync(ownerClient, "viewer@example.com", roles.Single(r => r.Key == "viewer").Id));
        using var memberClient = Client(await AddMemberAsync(ownerClient, "member@example.com", roles.Single(r => r.Key == "member").Id));
        var company = await PostAsync<CrmCompanyDto>(ownerClient, "/api/crm/companies", new SaveCrmCompanyRequest { Name = "星火科技" });
        var stages = await GetAsync<List<CrmStageDto>>(ownerClient, "/api/crm/stages");

        (await viewerClient.GetAsync(new Uri("/api/crm/companies", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewerClient.PostAsJsonAsync("/api/crm/companies", new SaveCrmCompanyRequest { Name = "不允许" }, PlatformJson.Options, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var memberContact = await PostAsync<CrmContactDto>(memberClient, "/api/crm/contacts", new SaveCrmContactRequest { Name = "王经理", CompanyId = company.Id });
        memberContact.Owner!.Name.Should().Be("member");
        (await memberClient.PutAsJsonAsync("/api/crm/stages", Save(stages), PlatformJson.Options, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 负责人只能是本空间的成员。
        var other = await RegisterAsync("other@example.com", "别的空间");
        using var otherClient = Client(other.AccessToken);
        var foreignOwner = await ownerClient.PostAsJsonAsync("/api/crm/companies", new SaveCrmCompanyRequest { Name = "x", OwnerUserId = other.Session.User.Id }, PlatformJson.Options, Ct);
        foreignOwner.IsSuccessStatusCode.Should().BeFalse();

        (await otherClient.GetAsync(new Uri($"/api/crm/companies/{company.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await otherClient.PostAsJsonAsync("/api/crm/contacts", new SaveCrmContactRequest { Name = "越界", CompanyId = company.Id }, PlatformJson.Options, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await otherClient.PostAsJsonAsync("/api/crm/deals", new SaveCrmDealRequest { Title = "越界", StageId = stages[0].Id }, PlatformJson.Options, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync<PagedResult<CrmCompanyDto>>(otherClient, "/api/crm/companies")).Items.Should().BeEmpty();
        (await GetAsync<List<CrmStageDto>>(otherClient, "/api/crm/stages")).Select(s => s.Id).Should().NotIntersectWith(stages.Select(s => s.Id));

        // 进销存依赖客户管理，要先停用它。
        var blocked = await ownerClient.PutAsJsonAsync("/api/modules/crm", new SetModuleEnabledRequest { Enabled = false }, PlatformJson.Options, Ct);
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(blocked)).Should().Be("module.required_by");
        await PutAsync<ModuleDto>(ownerClient, "/api/modules/erp", new SetModuleEnabledRequest { Enabled = false });
        await PutAsync<ModuleDto>(ownerClient, "/api/modules/crm", new SetModuleEnabledRequest { Enabled = false });
        (await ownerClient.GetAsync(new Uri("/api/crm/companies", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // 停用模块后，它的动作对智能体和对话也不再可见。
        (await PostAsync<InvokeActionResponse>(memberClient, "/api/actions/crm.search/invoke", new { query = "星火" })).Status.Should().Be("not_found");
    }

    [Fact]
    public async Task Search_matches_parts_of_names_and_treats_wildcards_literally()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        foreach (var name in new[] { "100%纯净水", "1000纯净水", "a_b 科技", "axb 科技", "星火科技" })
        {
            await PostAsync<CrmCompanyDto>(client, "/api/crm/companies", new SaveCrmCompanyRequest { Name = name });
        }

        await PostAsync<CrmContactDto>(client, "/api/crm/contacts", new SaveCrmContactRequest { Name = "王经理", Email = "Wang.Lei@Example.com" });

        (await SearchCompaniesAsync(client, "100%")).Should().Equal("100%纯净水");
        (await SearchCompaniesAsync(client, "a_b")).Should().Equal("a_b 科技");
        (await SearchCompaniesAsync(client, "科技")).Should().BeEquivalentTo("a_b 科技", "axb 科技", "星火科技");
        var contacts = await GetAsync<PagedResult<CrmContactDto>>(client, "/api/crm/contacts?search=wang.lei");
        contacts.Items.Should().ContainSingle(c => c.Name == "王经理");
    }

    [Fact]
    public async Task Deleting_keeps_related_records_and_system_activities_stay()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var stages = await StagesAsync(client);
        var company = await PostAsync<CrmCompanyDto>(client, "/api/crm/companies", new SaveCrmCompanyRequest { Name = "星火科技" });
        var contact = await PostAsync<CrmContactDto>(client, "/api/crm/contacts", new SaveCrmContactRequest { Name = "王经理", CompanyId = company.Id });
        var deal = await PostAsync<CrmDealDto>(client, "/api/crm/deals", new SaveCrmDealRequest { Title = "年度采购", CompanyId = company.Id });
        await PostAsync<CrmDealDto>(client, $"/api/crm/deals/{deal.Id}/stage", new MoveCrmDealRequest { StageId = stages["需求确认"].Id });
        await PostAsync<CrmActivityDto>(client, "/api/crm/activities", new LogCrmActivityRequest { Kind = CrmActivityKind.Note, Content = "只关于客户", CompanyId = company.Id });
        var stageChange = (await GetAsync<PagedResult<CrmActivityDto>>(client, $"/api/crm/activities?dealId={deal.Id}")).Items.Single();

        var refused = await client.DeleteAsync(new Uri($"/api/crm/activities/{stageChange.Id}", UriKind.Relative), Ct);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(refused)).Should().Be("crm_activity.system");

        (await client.DeleteAsync(new Uri($"/api/crm/companies/{company.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var orphanContact = await GetAsync<CrmContactDetailDto>(client, $"/api/crm/contacts/{contact.Id}");
        orphanContact.Contact.CompanyId.Should().BeNull();
        var orphanDeal = await GetAsync<CrmDealDetailDto>(client, $"/api/crm/deals/{deal.Id}");
        orphanDeal.Deal.CompanyId.Should().BeNull();
        orphanDeal.Activities.Should().ContainSingle(a => a.Kind == CrmActivityKind.StageChange);
        (await GetAsync<PagedResult<CrmActivityDto>>(client, "/api/crm/activities?pageSize=100")).Items.Should().NotContain(a => a.Content == "只关于客户");

        (await client.DeleteAsync(new Uri($"/api/crm/deals/{deal.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync(new Uri($"/api/crm/activities/{stageChange.Id}", UriKind.Relative), Ct)).IsSuccessStatusCode.Should().BeFalse();
        (await GetAsync<PagedResult<CrmActivityDto>>(client, "/api/crm/activities?pageSize=100")).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Associations_stay_consistent_and_last_activity_follows_deletes()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var acme = await PostAsync<CrmCompanyDto>(client, "/api/crm/companies", new SaveCrmCompanyRequest { Name = "星火科技" });
        var other = await PostAsync<CrmCompanyDto>(client, "/api/crm/companies", new SaveCrmCompanyRequest { Name = "远山集团" });
        var contact = await PostAsync<CrmContactDto>(client, "/api/crm/contacts", new SaveCrmContactRequest { Name = "王经理", CompanyId = acme.Id });
        var outsider = await PostAsync<CrmContactDto>(client, "/api/crm/contacts", new SaveCrmContactRequest { Name = "李总", CompanyId = other.Id });
        var deal = await PostAsync<CrmDealDto>(client, "/api/crm/deals", new SaveCrmDealRequest { Title = "年度采购", CompanyId = acme.Id, ContactId = contact.Id });

        // 跟进记录的客户、联系人要和商机一致，否则会出现在不相干的客户时间线上。
        var wrongCompany = await client.PostAsJsonAsync(
            "/api/crm/activities", new LogCrmActivityRequest { Kind = CrmActivityKind.Note, Content = "x", DealId = deal.Id, CompanyId = other.Id }, PlatformJson.Options, Ct);
        wrongCompany.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var wrongContact = await client.PostAsJsonAsync(
            "/api/crm/activities", new LogCrmActivityRequest { Kind = CrmActivityKind.Note, Content = "x", DealId = deal.Id, ContactId = outsider.Id }, PlatformJson.Options, Ct);
        wrongContact.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 删除最近一条跟进后，最近跟进时间回到剩下记录里最晚的一条。
        var earlier = await PostAsync<CrmActivityDto>(client, "/api/crm/activities", new LogCrmActivityRequest
        {
            Kind = CrmActivityKind.Meeting, Content = "上门拜访", DealId = deal.Id, OccurredAt = DateTimeOffset.UtcNow.AddDays(-3),
        });
        earlier.CompanyId.Should().Be(acme.Id);
        var latest = await PostAsync<CrmActivityDto>(client, "/api/crm/activities", new LogCrmActivityRequest { Kind = CrmActivityKind.Call, Content = "电话确认", DealId = deal.Id, ContactId = contact.Id });
        (await GetAsync<CrmDealDetailDto>(client, $"/api/crm/deals/{deal.Id}")).Deal.LastActivityAt.Should().BeCloseTo(latest.OccurredAt, TimeSpan.FromMilliseconds(1));
        (await client.DeleteAsync(new Uri($"/api/crm/activities/{latest.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GetAsync<CrmDealDetailDto>(client, $"/api/crm/deals/{deal.Id}")).Deal.LastActivityAt.Should().BeCloseTo(earlier.OccurredAt, TimeSpan.FromMilliseconds(1));
        var companyDetail = await GetAsync<CrmCompanyDetailDto>(client, $"/api/crm/companies/{acme.Id}");
        companyDetail.Company.LastActivityAt.Should().BeCloseTo(earlier.OccurredAt, TimeSpan.FromMilliseconds(1));
        companyDetail.ContactTotal.Should().Be(1);
        companyDetail.DealTotal.Should().Be(1);
        (await GetAsync<CrmContactDetailDto>(client, $"/api/crm/contacts/{contact.Id}")).Contact.LastActivityAt.Should().BeNull();

        // 联系人换到别的客户后，原客户的商机保留在原客户名下，但不再由这个人对接。
        var moved = await PutAsync<CrmContactDto>(client, $"/api/crm/contacts/{contact.Id}", new SaveCrmContactRequest
        {
            Name = "王经理", CompanyId = other.Id, OwnerUserId = contact.Owner?.Id,
        });
        moved.CompanyId.Should().Be(other.Id);
        var kept = await GetAsync<CrmDealDetailDto>(client, $"/api/crm/deals/{deal.Id}");
        kept.Deal.CompanyId.Should().Be(acme.Id);
        kept.Deal.ContactId.Should().BeNull();
        (await GetAsync<CrmContactDetailDto>(client, $"/api/crm/contacts/{contact.Id}")).DealTotal.Should().Be(0);
    }

    [Fact]
    public async Task Won_deal_triggers_a_workflow_filtered_on_the_target_stage()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var stages = await StagesAsync(client);
        var catalog = await GetAsync<WorkflowCatalogDto>(client, "/api/workflows/catalog");
        catalog.Events.Should().Contain(e => e.Key == "crm.deal_stage_changed");
        await PostAsync<WorkflowDto>(client, "/api/workflows", new SaveWorkflowRequest
        {
            Name = "赢单后安排交付",
            Trigger = new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Event, EventType = "crm.deal_stage_changed", Filter = "trigger.to.kind == 'won'" },
            Steps =
            [
                new WorkflowStep
                {
                    Key = "deliver",
                    Name = "建交付任务",
                    Kind = WorkflowStepKind.Action,
                    ActionKey = "tasks.create",
                    Input = Json("""{ "title": "交付 {{ trigger.deal.companyName }} {{ trigger.deal.title }}", "description": "{{ trigger.from.name }} → {{ trigger.to.name }}：{{ trigger.note }}" }"""),
                },
            ],
        });
        var company = await PostAsync<CrmCompanyDto>(client, "/api/crm/companies", new SaveCrmCompanyRequest { Name = "星火科技" });
        var deal = await PostAsync<CrmDealDto>(client, "/api/crm/deals", new SaveCrmDealRequest { Title = "年度采购", CompanyId = company.Id });

        await PostAsync<CrmDealDto>(client, $"/api/crm/deals/{deal.Id}/stage", new MoveCrmDealRequest { StageId = stages["商务谈判"].Id });
        await PostAsync<CrmDealDto>(client, $"/api/crm/deals/{deal.Id}/stage", new MoveCrmDealRequest { StageId = stages["赢单"].Id, Note = "签了三年" });

        var runs = await EventuallyAsync(
            () => GetAsync<PagedResult<WorkflowRunSummaryDto>>(client, "/api/workflows/runs"),
            r => r.Items.Count == 1 && r.Items[0].Status == WorkflowRunStatus.Succeeded);
        runs.Items[0].EventType.Should().Be("crm.deal_stage_changed");
        var task = (await GetAsync<PagedResult<WorkItemDto>>(client, "/api/work-items")).Items.Should().ContainSingle().Subject;
        task.Title.Should().Be("交付 星火科技 年度采购");
        task.Description.Should().Be("商务谈判 → 赢单：签了三年");

        await Task.Delay(500, Ct);
        (await GetAsync<PagedResult<WorkflowRunSummaryDto>>(client, "/api/workflows/runs")).Total.Should().Be(1);
    }

    [Fact]
    public async Task Actions_let_agents_and_chat_work_with_names_instead_of_ids()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);

        var created = await InvokeAsync(client, "crm.create_company", new { name = "星火科技", industry = "软件", tags = new[] { "重点客户" } });
        var companyId = created.GetProperty("id").GetGuid();
        var contact = await InvokeAsync(client, "crm.create_contact", new { name = "王经理", companyId, email = "wang@example.com" });
        var deal = await InvokeAsync(client, "crm.create_deal", new { title = "年度采购", contactId = contact.GetProperty("id").GetGuid(), stageName = "需求确认", amount = 80000 });
        var dealId = deal.GetProperty("id").GetGuid();
        deal.GetProperty("companyId").GetGuid().Should().Be(companyId);
        deal.GetProperty("stageName").GetString().Should().Be("需求确认");

        // 部分修改：只传要改的字段，其余保持不变。
        var updated = await InvokeAsync(client, "crm.update_company", new { companyId, phone = "021-12345678" });
        updated.GetProperty("phone").GetString().Should().Be("021-12345678");
        updated.GetProperty("industry").GetString().Should().Be("软件");
        updated.GetProperty("tags")[0].GetString().Should().Be("重点客户");
        var updatedDeal = await InvokeAsync(client, "crm.update_deal", new { dealId, amount = 90000 });
        updatedDeal.GetProperty("amount").GetDecimal().Should().Be(90000);
        updatedDeal.GetProperty("stageName").GetString().Should().Be("需求确认");

        var wrongStage = await PostAsync<InvokeActionResponse>(client, "/api/actions/crm.move_deal/invoke", new { dealId, stageName = "不存在" });
        wrongStage.Status.Should().Be("invalid_input");
        wrongStage.Error.Should().Contain("商务谈判");
        var won = await InvokeAsync(client, "crm.move_deal", new { dealId, stageName = "赢单", note = "预算批下来了" });
        won.GetProperty("status").GetString().Should().Be("won");

        var logged = await InvokeAsync(client, "crm.log_activity", new { kind = "meeting", content = "交付启动会", dealId });
        logged.GetProperty("companyId").GetGuid().Should().Be(companyId);

        var search = await InvokeAsync(client, "crm.search", new { query = "星火" });
        search.GetProperty("companies").GetArrayLength().Should().Be(1);
        search.GetProperty("deals").GetArrayLength().Should().Be(0);
        (await InvokeAsync(client, "crm.search", new { query = "年度" })).GetProperty("deals").GetArrayLength().Should().Be(1);

        var wonDeals = await InvokeAsync(client, "crm.list_deals", new { status = "won" });
        wonDeals.GetArrayLength().Should().Be(1);
        var pipeline = await InvokeAsync(client, "crm.get_pipeline", new { });
        pipeline.GetProperty("wonThisMonth")[0].GetProperty("amount").GetDecimal().Should().Be(90000);
        var detail = await InvokeAsync(client, "crm.get_company", new { companyId });
        detail.GetProperty("activities").GetArrayLength().Should().Be(2);
    }

    private static async Task<Dictionary<string, CrmStageDto>> StagesAsync(HttpClient client)
        => (await GetAsync<List<CrmStageDto>>(client, "/api/crm/stages")).ToDictionary(s => s.Name);

    private static SaveCrmStagesRequest Save(IEnumerable<CrmStageDto> stages) => new()
    {
        Stages = [.. stages.Select(s => new SaveCrmStageItem { Id = s.Id, Name = s.Name, Probability = s.Probability, Kind = s.Kind })],
    };

    private static async Task<List<string>> SearchCompaniesAsync(HttpClient client, string term)
        => [.. (await GetAsync<PagedResult<CrmCompanyDto>>(client, $"/api/crm/companies?search={Uri.EscapeDataString(term)}")).Items.Select(c => c.Name)];
}
