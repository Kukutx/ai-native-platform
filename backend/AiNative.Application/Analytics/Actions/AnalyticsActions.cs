using System.ComponentModel;
using AiNative.Application.Abstractions;
using AiNative.Application.Actions;
using AiNative.Contracts.Analytics;
using AiNative.Domain.Analytics;

namespace AiNative.Application.Analytics.Actions;

public sealed class ListMetricsInput;

internal sealed class ListMetricsAction(IRequestDispatcher dispatcher) : PlatformAction<ListMetricsInput, IReadOnlyList<MetricDefinitionDto>>
{
    public override string Key => "analytics.list_metrics";

    public override string ModuleKey => AnalyticsModule.ModuleKey;

    public override string Title => "查看可用指标";

    public override string Description => "列出你有权查看的经营指标：键、名称、统计口径、单位，以及可以按哪些维度拆分。用 analytics.query 查询具体数值前先看这里。";

    public override string RequiredFeature => AnalyticsFeatures.View;

    public override bool IsMutation => false;

    protected override Task<IReadOnlyList<MetricDefinitionDto>> ExecuteAsync(ListMetricsInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(new ListMetricsQuery(), cancellationToken);
}

public sealed class QueryMetricInput
{
    [Description("指标键，例如 crm.deals_won（赢单金额）、tasks.completed（完成任务）；全部可用指标用 analytics.list_metrics 查看")]
    public required string Metric { get; init; }

    [Description("快捷区间：last7_days 近 7 天、last30_days 近 30 天（默认）、last90_days 近 90 天、this_month 本月、last_month 上月、this_quarter 本季度、this_year 今年、last12_months 近 12 个月。按空间时区计算")]
    public AnalyticsRange? Range { get; init; }

    [Description("自定义区间的开始日期 yyyy-MM-dd（包含当天）；给了 from 或 to 时忽略 range")]
    public DateOnly? From { get; init; }

    [Description("自定义区间的结束日期 yyyy-MM-dd（包含当天），默认今天")]
    public DateOnly? To { get; init; }

    [Description("趋势粒度：day、week、month；不填时按区间长短自动选择")]
    public AnalyticsInterval? Interval { get; init; }

    [Description("拆分维度键（见 analytics.list_metrics），例如 owner 按负责人、product 按产品；不填只看总量")]
    public string? Breakdown { get; init; }

    [Description("金额类指标的货币代码，例如 CNY；不填时取区间内金额最多的货币")]
    public string? Currency { get; init; }

    [Description("拆分时最多列出几组，其余合并为「其他」，默认 5")]
    public int Limit { get; init; } = 5;
}

public sealed record MetricGroupOutput(string Label, decimal? Total);

/// <param name="ChangePercent">与上一个可比区间相比的变化百分比；上一区间为 0 或没有数据时为空。比率类指标是百分点差。</param>
public sealed record QueryMetricOutput(
    string Metric,
    string Name,
    MetricUnit Unit,
    string? Currency,
    IReadOnlyList<string> OtherCurrencies,
    DateOnly From,
    DateOnly To,
    string TimeZone,
    AnalyticsInterval Interval,
    decimal? Total,
    decimal? PreviousTotal,
    decimal? ChangePercent,
    IReadOnlyList<MetricPointDto> Trend,
    string? Breakdown,
    IReadOnlyList<MetricGroupOutput> Groups);

internal sealed class QueryMetricAction(IRequestDispatcher dispatcher, AnalyticsEngine engine) : PlatformAction<QueryMetricInput, QueryMetricOutput>
{
    public override string Key => "analytics.query";

    public override string ModuleKey => AnalyticsModule.ModuleKey;

    public override string Title => "查询经营指标";

    public override string Description =>
        "查询一个经营指标在某段时间的合计、与上一个可比区间（本月对上月同期、上月对再上一个月、近 30 天对再往前 30 天）的环比、趋势，以及按维度拆分的排名。" +
        "回答「这个月赢单多少」「近 30 天谁完成的任务最多」「上个月哪个产品卖得最好」「AI 花了多少钱」这类问题时使用。金额不同货币分开统计，不做汇率换算。";

    public override string RequiredFeature => AnalyticsFeatures.View;

    public override bool IsMutation => false;

    protected override async Task<QueryMetricOutput> ExecuteAsync(QueryMetricInput input, ActionContext context, CancellationToken cancellationToken)
    {
        var request = new MetricQueryRequest
        {
            Metric = input.Metric,
            Range = input.From is null && input.To is null ? input.Range ?? AnalyticsRange.Last30Days : null,
            From = input.From,
            To = input.To,
            Breakdown = input.Breakdown,
            Currency = input.Currency,
            Limit = Math.Clamp(input.Limit, 1, 20),
        };

        // 不指定粒度时按区间长短选，趋势保持在几十个点以内，不把上百个数据点塞给模型。
        var (_, period, _) = await engine.ResolvePeriodAsync(request.From, request.To, request.Range, null, cancellationToken).ConfigureAwait(false);
        var interval = input.Interval ?? (period.Days <= 31 ? AnalyticsInterval.Day : period.Days <= 180 ? AnalyticsInterval.Week : AnalyticsInterval.Month);
        var result = await dispatcher.SendAsync(new QueryMetricQuery(request with { Interval = interval }), cancellationToken).ConfigureAwait(false);

        decimal? change = null;
        if (result.Total is { } total && result.PreviousTotal is { } previous)
        {
            change = result.IsRatio ? Math.Round(total - previous, 1) : previous == 0 ? null : Math.Round((total - previous) * 100m / previous, 1);
        }

        return new QueryMetricOutput(
            result.Metric,
            result.Name,
            result.Unit,
            result.Currency,
            [.. result.Currencies.Where(c => c != result.Currency)],
            result.From,
            result.To,
            result.TimeZone,
            result.Interval,
            result.Total,
            result.PreviousTotal,
            change,
            result.Points,
            result.Breakdown,
            [.. result.Series.Select(s => new MetricGroupOutput(s.Label, s.Total))]);
    }
}

public sealed class GetOverviewInput
{
    [Description("快捷区间，取值同 analytics.query，默认 last30_days")]
    public AnalyticsRange? Range { get; init; }

    [Description("自定义区间的开始日期 yyyy-MM-dd")]
    public DateOnly? From { get; init; }

    [Description("自定义区间的结束日期 yyyy-MM-dd")]
    public DateOnly? To { get; init; }
}

internal sealed class GetOverviewAction(IRequestDispatcher dispatcher) : PlatformAction<GetOverviewInput, AnalyticsOverviewDto>
{
    public override string Key => "analytics.get_overview";

    public override string ModuleKey => AnalyticsModule.ModuleKey;

    public override string Title => "查看经营概览";

    public override string Description =>
        "一次看全局：关键指标的区间合计与环比（赢单金额、赢率、新增商机、发货金额、完成任务、智能体执行、AI 花费、工作流运行），" +
        "以及当前的销售漏斗、缺货产品、进行中项目的健康度和各智能体的工作量。写周报、月报或回答「最近经营情况怎么样」时先用它。";

    public override string RequiredFeature => AnalyticsFeatures.View;

    public override bool IsMutation => false;

    protected override Task<AnalyticsOverviewDto> ExecuteAsync(GetOverviewInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(
            new GetAnalyticsOverviewQuery(input.From, input.To, input.From is null && input.To is null ? input.Range ?? AnalyticsRange.Last30Days : null),
            cancellationToken);
}
