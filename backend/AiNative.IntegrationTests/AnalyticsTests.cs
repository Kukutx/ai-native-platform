using System.Net;
using System.Net.Http.Json;
using AiNative.Application.Common;
using AiNative.Contracts.Analytics;
using AiNative.Contracts.Auth;
using AiNative.Contracts.Crm;
using AiNative.Contracts.Erp;
using AiNative.Contracts.Platform;
using AiNative.Contracts.Projects;
using AiNative.Contracts.Work;
using AiNative.Domain.Analytics;
using AiNative.Domain.Work;
using AiNative.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AiNative.IntegrationTests;

public sealed class AnalyticsTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Won_deals_count_on_the_local_day_of_the_space_and_currencies_stay_apart()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        auth.Session.Tenant.TimeZone.Should().Be("UTC");

        // Windows 时区名也能识别，统一存成 IANA 名称。
        var settings = await PutAsync<TenantSettingsDto>(client, "/api/tenant/settings", new UpdateTenantSettingsRequest { Name = "上海分部", TimeZone = "China Standard Time" });
        settings.TimeZone.Should().Be("Asia/Shanghai");
        var session = await GetAsync<SessionDto>(client, "/api/auth/session");
        (session.Tenant.Name, session.Tenant.TimeZone).Should().Be(("上海分部", "Asia/Shanghai"));
        var invalid = await client.PutAsJsonAsync("/api/tenant/settings", new UpdateTenantSettingsRequest { Name = "上海分部", TimeZone = "Mars/Olympus" }, PlatformJson.Options, Ct);
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var stages = await StagesAsync(client);
        await ClosedDealAsync(client, stages["赢单"], 1000, "CNY", "2026-09-30T17:00:00Z"); // 上海 10 月 1 日 01:00
        await ClosedDealAsync(client, stages["赢单"], 500, "CNY", "2026-10-01T15:00:00Z"); // 上海 10 月 1 日 23:00
        await ClosedDealAsync(client, stages["赢单"], 300, "USD", "2026-09-30T15:00:00Z"); // 上海 9 月 30 日 23:00
        await ClosedDealAsync(client, stages["输单"], 900, "CNY", "2026-10-02T02:00:00Z");

        var week = await QueryAsync(client, "metric=crm.deals_won&from=2026-10-01&to=2026-10-07");
        week.TimeZone.Should().Be("Asia/Shanghai");
        week.Currency.Should().Be("CNY");
        week.Currencies.Should().Equal("CNY");
        week.Total.Should().Be(1500);
        week.Points.Should().HaveCount(7);
        week.Points[0].Should().Be(new MetricPointDto(new DateOnly(2026, 10, 1), 1500));
        week.Points[1].Value.Should().Be(0);

        // 自定义区间对比紧挨着的前 7 天，只比同一种货币。
        week.PreviousTotal.Should().Be(0);
        var usd = await QueryAsync(client, "metric=crm.deals_won&from=2026-10-01&to=2026-10-07&currency=usd");
        (usd.Currency, usd.Total, usd.PreviousTotal).Should().Be(("USD", 0m, 300m));

        // 按 UTC 统计时第一笔落在 9 月 30 日。
        (await QueryAsync(client, "metric=crm.deals_won&from=2026-10-01&to=2026-10-07&timeZone=UTC")).Total.Should().Be(500);

        // 区间里有多种货币时默认取金额最大的；按周统计时第一周从周一算起。
        var both = await QueryAsync(client, "metric=crm.deals_won&from=2026-09-28&to=2026-10-04&interval=week");
        both.Currencies.Should().Equal("CNY", "USD");
        both.Points.Should().Equal(new MetricPointDto(new DateOnly(2026, 9, 28), 1500));

        // 赢率是区间内关闭的商机里赢单的比例，没有关闭商机的日子没有值。
        var winRate = await QueryAsync(client, "metric=crm.win_rate&from=2026-10-01&to=2026-10-07");
        winRate.IsRatio.Should().BeTrue();
        winRate.Total.Should().Be(66.7m);
        winRate.Points.Take(3).Select(p => p.Value).Should().Equal(100m, 0m, null);

        var tooLong = await client.GetAsync(new Uri("/api/analytics/query?metric=crm.deals_won&from=2024-01-01&to=2026-01-01", UriKind.Relative), Ct);
        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Breakdowns_keep_the_top_groups_and_hide_what_the_viewer_cannot_see()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var project = (await PostAsync<ProjectDetailDto>(client, "/api/projects", new SaveProjectRequest { Name = "官网改版" })).Project;
        WorkItemPriority[] priorities =
        [
            WorkItemPriority.Urgent, WorkItemPriority.Urgent, WorkItemPriority.Urgent, WorkItemPriority.High,
            WorkItemPriority.High, WorkItemPriority.Low, WorkItemPriority.Medium,
        ];
        for (var i = 0; i < priorities.Length; i++)
        {
            await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest
            {
                Title = $"任务 {i}",
                Priority = priorities[i],
                Status = WorkItemStatus.Done,
                ProjectId = i < 2 ? project.Id : null,
            });
        }

        await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = "还在做", Priority = WorkItemPriority.Urgent });

        var byPriority = await QueryAsync(client, "metric=tasks.completed&range=last7_days&breakdown=priority&limit=2");
        byPriority.Total.Should().Be(7);
        byPriority.Series.Select(s => (s.Label, s.Total)).Should().Equal(("紧急", 3m), ("高", 2m), ("其他", 2m));
        byPriority.Series[^1].Key.Should().Be("__other__");
        byPriority.Series.Sum(s => s.Points.Sum(p => p.Value)).Should().Be(7);
        (await QueryAsync(client, "metric=tasks.created&range=last7_days")).Total.Should().Be(8);

        var byProject = await QueryAsync(client, "metric=tasks.completed&range=last7_days&breakdown=project");
        byProject.Series.Select(s => (s.Label, s.Total)).Should().Equal(("不属于项目", 5m), ("官网改版", 2m));

        // 只能看任务的角色：看不到客户和花费指标，也不能按项目或智能体拆分。
        var role = await PostAsync<RoleDto>(client, "/api/roles", new SaveRoleRequest { Key = "analyst", Name = "分析员", Features = ["tasks.view", "analytics.view"] });
        using var analyst = Client(await AddMemberAsync(client, "analyst@example.com", role.Id));
        var metrics = await GetAsync<List<MetricDefinitionDto>>(analyst, "/api/analytics/metrics");
        metrics.Select(m => m.Key).Should().Contain("tasks.completed").And.NotContain(k => k.StartsWith("crm.") || k.StartsWith("ai."));
        metrics.Single(m => m.Key == "tasks.completed").Dimensions.Select(d => d.Key).Should().Equal("priority");
        (await analyst.GetAsync(new Uri("/api/analytics/query?metric=crm.deals_won", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await analyst.GetAsync(new Uri("/api/analytics/query?metric=tasks.completed&breakdown=project", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var missing = await analyst.GetAsync(new Uri("/api/analytics/query?metric=crm.unknown", UriKind.Relative), Ct);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeAsync(missing)).Should().Be("metric.not_found");
        (await QueryAsync(analyst, "metric=tasks.completed&range=last7_days")).Total.Should().Be(7);

        // 没有经营分析权限的角色完全用不了。
        var plain = await PostAsync<RoleDto>(client, "/api/roles", new SaveRoleRequest { Key = "plain", Name = "普通", Features = ["tasks.view"] });
        using var outsider = Client(await AddMemberAsync(client, "plain@example.com", plain.Id));
        (await outsider.GetAsync(new Uri("/api/analytics/metrics", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 关闭项目管理模块后不再提供按项目拆分。
        await PutAsync<ModuleDto>(client, "/api/modules/projects", new SetModuleEnabledRequest { Enabled = false });
        (await GetAsync<List<MetricDefinitionDto>>(client, "/api/analytics/metrics")).Single(m => m.Key == "tasks.completed")
            .Dimensions.Select(d => d.Key).Should().Equal("agent", "priority");

        // 其他空间的数据不计入。
        var other = await RegisterAsync("other@example.com", "别家");
        using var otherClient = Client(other.AccessToken);
        (await QueryAsync(otherClient, "metric=tasks.completed&range=last7_days")).Total.Should().Be(0);
    }

    [Fact]
    public async Task Reports_are_saved_reordered_and_protected_from_concurrent_edits()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var won = await PostAsync<AnalyticsReportDto>(client, "/api/analytics/reports", new SaveAnalyticsReportRequest
        {
            Name = "每月赢单",
            Metric = "crm.deals_won",
            Breakdown = "owner",
            Currency = "cny",
            Range = AnalyticsRange.ThisYear,
            Interval = AnalyticsInterval.Month,
            Chart = AnalyticsChart.Bar,
        });
        (won.Currency, won.Position, won.CreatedBy.Id).Should().Be(("CNY", 0, auth.Session.User.Id));
        var tasks = await PostAsync<AnalyticsReportDto>(client, "/api/analytics/reports", new SaveAnalyticsReportRequest { Name = "任务完成", Metric = "tasks.completed" });
        var cost = await PostAsync<AnalyticsReportDto>(client, "/api/analytics/reports", new SaveAnalyticsReportRequest { Name = "AI 花费", Metric = "ai.cost", Breakdown = "model" });
        (tasks.Position, cost.Position).Should().Be((1, 2));
        tasks.Range.Should().Be(AnalyticsRange.Last30Days);

        var badBreakdown = await client.PostAsJsonAsync("/api/analytics/reports", new SaveAnalyticsReportRequest { Name = "x", Metric = "crm.deals_won", Breakdown = "priority" }, PlatformJson.Options, Ct);
        badBreakdown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var unknown = await client.PostAsJsonAsync("/api/analytics/reports", new SaveAnalyticsReportRequest { Name = "x", Metric = "nope" }, PlatformJson.Options, Ct);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var renamed = await PutAsync<AnalyticsReportDto>(client, $"/api/analytics/reports/{won.Id}", Save(won) with { Name = "每月赢单（按负责人）", ExpectedUpdatedAt = won.UpdatedAt });
        renamed.Name.Should().Be("每月赢单（按负责人）");
        var stale = await client.PutAsJsonAsync($"/api/analytics/reports/{won.Id}", Save(won) with { ExpectedUpdatedAt = won.UpdatedAt }, PlatformJson.Options, Ct);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(stale)).Should().Be("analytics_report.modified");

        // 没列出的报表排在后面，保持原来的先后。
        var reordered = await PutAsync<List<AnalyticsReportDto>>(client, "/api/analytics/reports/order", new ReorderAnalyticsReportsRequest { Ids = [cost.Id, won.Id] });
        reordered.Select(r => (r.Name, r.Position)).Should().Equal(("AI 花费", 0), ("每月赢单（按负责人）", 1), ("任务完成", 2));
        (await GetAsync<List<AnalyticsReportDto>>(client, "/api/analytics/reports")).Select(r => r.Id).Should().Equal(cost.Id, won.Id, tasks.Id);

        (await client.DeleteAsync(new Uri($"/api/analytics/reports/{tasks.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync(new Uri($"/api/analytics/reports/{tasks.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.DeleteAsync(new Uri($"/api/analytics/reports/{tasks.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 只读成员能看报表，不能改。
        var viewerRole = (await GetAsync<List<RoleDto>>(client, "/api/roles")).Single(r => r.Key == "viewer");
        using var viewer = Client(await AddMemberAsync(client, "viewer@example.com", viewerRole.Id));
        (await GetAsync<List<AnalyticsReportDto>>(viewer, "/api/analytics/reports")).Should().HaveCount(2);
        var forbidden = await viewer.PostAsJsonAsync("/api/analytics/reports", new SaveAnalyticsReportRequest { Name = "x", Metric = "crm.deals_won" }, PlatformJson.Options, Ct);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 其他空间看不到。
        var other = await RegisterAsync("other@example.com", "别家");
        using var otherClient = Client(other.AccessToken);
        (await GetAsync<List<AnalyticsReportDto>>(otherClient, "/api/analytics/reports")).Should().BeEmpty();
        (await otherClient.GetAsync(new Uri($"/api/analytics/reports/{won.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Overview_and_actions_report_the_same_numbers()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var stages = await StagesAsync(client);
        var deal = await PostAsync<CrmDealDto>(client, "/api/crm/deals", new SaveCrmDealRequest { Title = "年度采购", Amount = 2000 });
        await PostAsync<CrmDealDto>(client, $"/api/crm/deals/{deal.Id}/stage", new MoveCrmDealRequest { StageId = stages["赢单"].Id });
        await PostAsync<CrmDealDto>(client, "/api/crm/deals", new SaveCrmDealRequest { Title = "试用转正", Amount = 800 });

        var service = await PostAsync<ErpProductDto>(client, "/api/erp/products", new SaveErpProductRequest { Sku = "SVC", Name = "安装服务", Unit = "次", Price = 100, TrackInventory = false });
        await PostAsync<ErpProductDto>(client, "/api/erp/products", new SaveErpProductRequest { Sku = "W-1", Name = "零件", Price = 10, ReorderPoint = 5 });
        var order = await PostAsync<ErpSalesOrderDto>(client, "/api/erp/sales-orders?confirm=true", new SaveErpSalesOrderRequest
        {
            Lines = [new ErpOrderLineRequest { ProductId = service.Id, Quantity = 3 }],
        });
        await PostAsync<ErpSalesOrderDto>(client, $"/api/erp/sales-orders/{order.Id}/ship", new { });
        for (var i = 0; i < 2; i++)
        {
            await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest { Title = $"完成 {i}", Status = WorkItemStatus.Done });
        }

        var overview = await GetAsync<AnalyticsOverviewDto>(client, "/api/analytics/overview?range=this_month");
        var kpis = overview.Kpis.ToDictionary(k => k.Metric);
        (kpis["crm.deals_won"].Value, kpis["crm.deals_won"].Currency).Should().Be((2000m, "CNY"));
        kpis["crm.win_rate"].Value.Should().Be(100);
        kpis["erp.sales_shipped"].Value.Should().Be(300);
        kpis["tasks.completed"].Value.Should().Be(2);
        kpis["crm.deals_created"].PreviousValue.Should().Be(0);
        overview.Pipeline!.Single(s => s.Name == "线索").Amounts.Should().Equal(new AnalyticsAmountDto("CNY", 800));
        overview.LowStock!.Should().ContainSingle().Which.Sku.Should().Be("W-1");
        overview.Projects.Should().BeEmpty();
        overview.Agents!.Should().ContainSingle();

        // 智能体和对话调用的动作与界面同一口径。
        var query = await InvokeAsync(client, "analytics.query", new { metric = "erp.sales_shipped", range = "last7_days", breakdown = "product" });
        query.GetProperty("total").GetDecimal().Should().Be(300);
        query.GetProperty("interval").GetString().Should().Be("day");
        query.GetProperty("trend").GetArrayLength().Should().Be(7);
        query.GetProperty("groups")[0].GetProperty("label").GetString().Should().Be("SVC 安装服务");
        var listed = await InvokeAsync(client, "analytics.list_metrics", new { });
        listed.EnumerateArray().Select(m => m.GetProperty("key").GetString()).Should().Contain(["crm.deals_won", "erp.sales_shipped", "tasks.completed", "ai.cost"]);
        var viaAction = await InvokeAsync(client, "analytics.get_overview", new { range = "this_month" });
        viaAction.GetProperty("kpis").GetArrayLength().Should().Be(overview.Kpis.Count);

        // 关闭进销存后概览里不再有相关指标和库存预警。
        await PutAsync<ModuleDto>(client, "/api/modules/erp", new SetModuleEnabledRequest { Enabled = false });
        overview = await GetAsync<AnalyticsOverviewDto>(client, "/api/analytics/overview?range=this_month");
        overview.Kpis.Select(k => k.Metric).Should().NotContain("erp.sales_shipped");
        overview.LowStock.Should().BeNull();
    }

    [Fact]
    public async Task Every_metric_can_be_queried_with_each_of_its_breakdowns()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var stages = await StagesAsync(client);
        var company = await PostAsync<CrmCompanyDto>(client, "/api/crm/companies", new SaveCrmCompanyRequest { Name = "星火科技" });
        var deal = await PostAsync<CrmDealDto>(client, "/api/crm/deals", new SaveCrmDealRequest { Title = "年度采购", CompanyId = company.Id, Amount = 1200 });
        await PostAsync<CrmDealDto>(client, $"/api/crm/deals/{deal.Id}/stage", new MoveCrmDealRequest { StageId = stages["赢单"].Id });

        var metrics = await GetAsync<List<MetricDefinitionDto>>(client, "/api/analytics/metrics");
        metrics.Select(m => m.ModuleKey).Distinct().Should().BeEquivalentTo(["crm", "erp", "agents", "platform", "workflows"]);
        foreach (var metric in metrics)
        {
            foreach (var breakdown in metric.Dimensions.Select(d => d.Key).Prepend(null))
            {
                var result = await QueryAsync(client, $"metric={metric.Key}&range=last90_days&interval=week{(breakdown is null ? "" : $"&breakdown={breakdown}")}");
                (result.Metric, result.Breakdown).Should().Be((metric.Key, breakdown));
            }
        }

        // 维度值换成名称：负责人显示成员名，客户显示公司名。
        (await QueryAsync(client, "metric=crm.deals_won&range=last7_days&breakdown=owner")).Series.Should().ContainSingle().Which.Label.Should().Be("owner");
        (await QueryAsync(client, "metric=crm.deals_won&range=last7_days&breakdown=company")).Series.Should().ContainSingle().Which.Label.Should().Be("星火科技");
    }

    private static Task<MetricResultDto> QueryAsync(HttpClient client, string query) => GetAsync<MetricResultDto>(client, $"/api/analytics/query?{query}");

    private static async Task<Dictionary<string, CrmStageDto>> StagesAsync(HttpClient client)
        => (await GetAsync<List<CrmStageDto>>(client, "/api/crm/stages")).ToDictionary(s => s.Name);

    /// <summary>
    /// 建一个商机并关闭到指定阶段，再把关闭时间改成给定时刻，方便验证按天统计的边界。
    /// </summary>
    private async Task ClosedDealAsync(HttpClient client, CrmStageDto stage, decimal amount, string currency, string closedAt)
    {
        var deal = await PostAsync<CrmDealDto>(client, "/api/crm/deals", new SaveCrmDealRequest { Title = $"商机 {closedAt}", Amount = amount, Currency = currency });
        await PostAsync<CrmDealDto>(client, $"/api/crm/deals/{deal.Id}/stage", new MoveCrmDealRequest { StageId = stage.Id });
        var at = DateTimeOffset.Parse(closedAt, System.Globalization.CultureInfo.InvariantCulture);
        await WithDbAsync(db => db.CrmDeals.IgnoreQueryFilters().Where(d => d.Id == deal.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ClosedAt, at), Ct));
    }

    private static SaveAnalyticsReportRequest Save(AnalyticsReportDto r) => new()
    {
        Name = r.Name,
        Description = r.Description,
        Metric = r.Metric,
        Breakdown = r.Breakdown,
        Currency = r.Currency,
        Range = r.Range,
        Interval = r.Interval,
        Chart = r.Chart,
    };
}
