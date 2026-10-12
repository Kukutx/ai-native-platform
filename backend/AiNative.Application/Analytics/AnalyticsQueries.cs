using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Agents.Actions;
using AiNative.Application.Common;
using AiNative.Application.Crm;
using AiNative.Application.Erp;
using AiNative.Application.Modules;
using AiNative.Application.Projects;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Analytics;
using AiNative.Domain.Agents;
using AiNative.Domain.Analytics;
using AiNative.Domain.Common;
using AiNative.Domain.Crm;
using AiNative.Domain.Projects;
using AiNative.Domain.Work;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Analytics;

[RequireFeature(AnalyticsFeatures.View)]
public sealed record ListMetricsQuery : IQuery<IReadOnlyList<MetricDefinitionDto>>;

[RequireFeature(AnalyticsFeatures.View)]
public sealed record QueryMetricQuery(MetricQueryRequest Request) : IQuery<MetricResultDto>;

[RequireFeature(AnalyticsFeatures.View)]
public sealed record GetAnalyticsOverviewQuery(DateOnly? From = null, DateOnly? To = null, AnalyticsRange? Range = null, string? TimeZone = null)
    : IQuery<AnalyticsOverviewDto>;

public sealed class QueryMetricQueryValidator : AbstractValidator<QueryMetricQuery>
{
    public QueryMetricQueryValidator()
    {
        RuleFor(x => x.Request.Metric).NotEmpty().WithMessage("请指定指标。").MaximumLength(64);
        RuleFor(x => x.Request.Breakdown).MaximumLength(32);
        RuleFor(x => x.Request.Currency!).Matches("^[A-Za-z]{3}$").When(x => !string.IsNullOrWhiteSpace(x.Request.Currency))
            .WithMessage("货币用三位字母代码，例如 CNY。");
        RuleFor(x => x.Request.TimeZone).MaximumLength(64);
        RuleFor(x => x.Request.Limit).InclusiveBetween(1, 20);
    }
}

public sealed class GetAnalyticsOverviewQueryValidator : AbstractValidator<GetAnalyticsOverviewQuery>
{
    public GetAnalyticsOverviewQueryValidator()
    {
        RuleFor(x => x.TimeZone).MaximumLength(64);
    }
}

internal sealed class AnalyticsQueryHandlers(
    IAppDbContext db,
    ITenantContext tenant,
    IModuleAccess modules,
    MetricCatalog catalog,
    AnalyticsEngine engine,
    TenantCalendar calendar) :
    IRequestHandler<ListMetricsQuery, IReadOnlyList<MetricDefinitionDto>>,
    IRequestHandler<QueryMetricQuery, MetricResultDto>,
    IRequestHandler<GetAnalyticsOverviewQuery, AnalyticsOverviewDto>
{
    /// <summary>
    /// 概览里的关键指标，按这个顺序展示；查看者看不到的会被跳过。
    /// </summary>
    internal static readonly string[] OverviewKpis =
    [
        "crm.deals_won", "crm.win_rate", "crm.deals_created", "erp.sales_shipped",
        "tasks.completed", "agents.runs", "ai.cost", "workflows.runs",
    ];

    public async Task<IReadOnlyList<MetricDefinitionDto>> HandleAsync(ListMetricsQuery request, CancellationToken cancellationToken)
    {
        var metrics = await catalog.AvailableAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<MetricDefinitionDto>(metrics.Count);
        foreach (var metric in metrics)
        {
            var dimensions = await catalog.DimensionsAsync(metric, cancellationToken).ConfigureAwait(false);
            result.Add(new MetricDefinitionDto(
                metric.Key, metric.ModuleKey, metric.Name, metric.Description, metric.Unit, metric.IsRatio,
                [.. dimensions.Select(d => new MetricDimensionDto(d.Key, d.Name))]));
        }

        return result;
    }

    public Task<MetricResultDto> HandleAsync(QueryMetricQuery request, CancellationToken cancellationToken)
        => engine.QueryAsync(request.Request, cancellationToken);

    public async Task<AnalyticsOverviewDto> HandleAsync(GetAnalyticsOverviewQuery request, CancellationToken cancellationToken)
    {
        var (zoneId, period, range) = await engine.ResolvePeriodAsync(request.From, request.To, request.Range, request.TimeZone, cancellationToken)
            .ConfigureAwait(false);
        if (period.Days > AnalyticsEngine.MaxDays(AnalyticsInterval.Day))
        {
            throw new ValidationFailedException(new Dictionary<string, string[]> { ["to"] = ["概览的区间最长 366 天。"] });
        }

        var available = (await catalog.AvailableAsync(cancellationToken).ConfigureAwait(false)).Select(m => m.Key).ToHashSet(StringComparer.Ordinal);
        var kpis = new List<AnalyticsKpiDto>();
        foreach (var key in OverviewKpis.Where(available.Contains))
        {
            // 快捷区间原样传下去，环比按月、季、年对齐；自定义区间对比等长的上一段。
            var result = await engine.QueryAsync(
                new MetricQueryRequest
                {
                    Metric = key,
                    From = range is null ? period.From : null,
                    To = range is null ? period.To : null,
                    Range = range,
                    TimeZone = zoneId,
                    Interval = AnalyticsInterval.Month,
                },
                cancellationToken).ConfigureAwait(false);
            kpis.Add(new AnalyticsKpiDto(result.Metric, result.Name, result.Unit, result.IsRatio, result.Currency, result.Total, result.PreviousTotal));
        }

        var enabled = await modules.GetEnabledAsync(cancellationToken).ConfigureAwait(false);
        var zone = TimeZones.Find(zoneId)!;
        var from = TimeZones.StartOfDay(period.From, zone);
        var to = TimeZones.StartOfDay(period.To.AddDays(1), zone);

        return new AnalyticsOverviewDto(
            period.From,
            period.To,
            zoneId,
            kpis,
            enabled.Contains(CrmModule.ModuleKey) && tenant.Can(CrmFeatures.View) ? await PipelineAsync(cancellationToken).ConfigureAwait(false) : null,
            enabled.Contains(ErpModule.ModuleKey) && tenant.Can(ErpFeatures.View) ? await LowStockAsync(cancellationToken).ConfigureAwait(false) : null,
            enabled.Contains(ProjectsModule.ModuleKey) && tenant.Can(ProjectFeatures.View) ? await ProjectsAsync(cancellationToken).ConfigureAwait(false) : null,
            enabled.Contains(AgentsModuleKeys.Agents) && tenant.Can(Features.Agents.View) && tenant.Can(Features.Runs.View)
                ? await AgentsAsync(from, to, cancellationToken).ConfigureAwait(false)
                : null);
    }

    private async Task<IReadOnlyList<PipelineStageSummaryDto>> PipelineAsync(CancellationToken cancellationToken)
    {
        var stages = await db.CrmStages.AsNoTracking().Where(s => s.Kind == CrmStageKind.Open).OrderBy(s => s.Position)
            .Select(s => new { s.Id, s.Name, s.Probability })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var sums = await db.CrmDeals.AsNoTracking().Where(d => d.Status == CrmDealStatus.Open)
            .GroupBy(d => new { d.StageId, d.Currency })
            .Select(g => new { g.Key.StageId, g.Key.Currency, Count = g.Count(), Amount = g.Sum(d => d.Amount ?? 0) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. stages.Select(s =>
        {
            var rows = sums.Where(x => x.StageId == s.Id).ToList();
            return new PipelineStageSummaryDto(
                s.Id,
                s.Name,
                s.Probability,
                rows.Sum(x => x.Count),
                [.. rows.Where(x => x.Amount != 0).OrderByDescending(x => x.Amount).Select(x => new AnalyticsAmountDto(x.Currency.Trim(), x.Amount))]);
        })];
    }

    private async Task<IReadOnlyList<LowStockItemDto>> LowStockAsync(CancellationToken cancellationToken)
    {
        var items = await db.ErpProducts.AsNoTracking()
            .Where(p => p.IsActive && p.TrackInventory && p.ReorderPoint != null)
            .Select(p => new
            {
                p.Id,
                p.Sku,
                p.Name,
                p.Unit,
                ReorderPoint = p.ReorderPoint!.Value,
                Available = db.ErpStockLevels.Where(l => l.ProductId == p.Id).Sum(l => (decimal?)(l.OnHand - l.Reserved)) ?? 0,
            })
            .Where(x => x.Available <= x.ReorderPoint)
            .OrderBy(x => x.Available - x.ReorderPoint).ThenBy(x => x.Sku)
            .Take(10)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. items.Select(x => new LowStockItemDto(x.Id, x.Sku, x.Name, x.Unit, x.Available, x.ReorderPoint))];
    }

    private async Task<IReadOnlyList<ProjectHealthDto>> ProjectsAsync(CancellationToken cancellationToken)
    {
        var projects = await db.Projects.AsNoTracking()
            .Where(p => p.Status == ProjectStatus.Active || p.Status == ProjectStatus.Planned || p.Status == ProjectStatus.OnHold)
            .OrderBy(p => p.Status == ProjectStatus.Active ? 0 : p.Status == ProjectStatus.Planned ? 1 : 2)
            .ThenBy(p => p.TargetDate == null).ThenBy(p => p.TargetDate)
            .Take(8)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var today = await calendar.TodayAsync(cancellationToken).ConfigureAwait(false);
        var dtos = await ProjectMapping.ProjectsAsync(db, projects, today, cancellationToken).ConfigureAwait(false);
        return [.. dtos.Select(p => new ProjectHealthDto(
            p.Id, p.Name, p.Status, p.Progress.Percent, p.Progress.Total - p.Progress.Done, p.Progress.Blocked, p.Progress.Overdue, p.TargetDate, p.IsOverdue))];
    }

    private async Task<IReadOnlyList<AgentActivityDto>> AgentsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var agents = await db.Agents.AsNoTracking().Where(a => a.Status != AgentStatus.Terminated)
            .Select(a => new { a.Id, a.Name, a.Title })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var open = await db.WorkItems.AsNoTracking()
            .Where(t => t.AssigneeAgentId != null && t.Status != WorkItemStatus.Done && t.Status != WorkItemStatus.Cancelled)
            .GroupBy(t => t.AssigneeAgentId!.Value)
            .Select(g => new { AgentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AgentId, x => x.Count, cancellationToken).ConfigureAwait(false);
        var runs = await db.HeartbeatRuns.AsNoTracking()
            .Where(r => r.CreatedAt >= from && r.CreatedAt < to)
            .GroupBy(r => r.AgentId)
            .Select(g => new { AgentId = g.Key, Count = g.Count(), Failed = g.Count(r => r.Status == RunStatus.Failed || r.Status == RunStatus.TimedOut) })
            .ToDictionaryAsync(x => x.AgentId, cancellationToken).ConfigureAwait(false);
        var costs = await db.CostEvents.AsNoTracking()
            .Where(c => c.Actor.Kind == ActorKind.Agent && c.OccurredAt >= from && c.OccurredAt < to)
            .GroupBy(c => c.Actor.Id!.Value)
            .Select(g => new { AgentId = g.Key, Cost = g.Sum(c => c.CostUsd) })
            .ToDictionaryAsync(x => x.AgentId, x => x.Cost, cancellationToken).ConfigureAwait(false);
        return [.. agents
            .Select(a => new AgentActivityDto(
                a.Id,
                a.Name,
                a.Title,
                open.GetValueOrDefault(a.Id),
                runs.TryGetValue(a.Id, out var r) ? r.Count : 0,
                runs.TryGetValue(a.Id, out var f) ? f.Failed : 0,
                costs.GetValueOrDefault(a.Id)))
            .OrderByDescending(a => a.Runs).ThenByDescending(a => a.OpenTasks).ThenBy(a => a.Name, StringComparer.Ordinal)
            .Take(10)];
    }
}
