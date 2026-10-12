using AiNative.Domain.Common;

namespace AiNative.Domain.Analytics;

/// <summary>
/// 统计粒度：按天、按周（周一开始）或按月汇总。
/// </summary>
public enum AnalyticsInterval
{
    Day = 0,
    Week = 1,
    Month = 2,
}

/// <summary>
/// 统计区间的快捷选项，按空间时区的"今天"推算，所以保存的报表打开时总是最新的区间。
/// </summary>
public enum AnalyticsRange
{
    Last7Days = 0,
    Last30Days = 1,
    Last90Days = 2,
    ThisMonth = 3,
    LastMonth = 4,
    ThisQuarter = 5,
    ThisYear = 6,
    Last12Months = 7,
}

public enum AnalyticsChart
{
    Line = 0,
    Bar = 1,
}

/// <summary>
/// 保存下来的报表：一个指标加上区间、粒度、拆分维度和图表样式。空间内共享，显示在经营分析的"报表"里。
/// 只存查询条件，不存结果，打开时按当前数据和查看者的权限实时计算。
/// </summary>
public sealed class AnalyticsReport : TenantEntity
{
    public required string Name { get; set; }

    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 指标键，如 crm.deals_won。
    /// </summary>
    public required string Metric { get; set; }

    /// <summary>
    /// 拆分维度键，如 owner；为空时只看总量。
    /// </summary>
    public string? Breakdown { get; set; }

    /// <summary>
    /// 金额类指标的货币；为空时取区间内金额最多的货币。
    /// </summary>
    public string? Currency { get; set; }

    public AnalyticsRange Range { get; set; } = AnalyticsRange.Last30Days;

    public AnalyticsInterval Interval { get; set; } = AnalyticsInterval.Day;

    public AnalyticsChart Chart { get; set; } = AnalyticsChart.Line;

    /// <summary>
    /// 在报表列表里的顺序，越小越靠前。
    /// </summary>
    public int Position { get; set; }

    public Actor CreatedBy { get; set; }
}
