using AiNative.Contracts.Common;
using AiNative.Domain.Analytics;
using AiNative.Domain.Projects;

namespace AiNative.Contracts.Analytics;

/// <summary>
/// 指标的单位：count 次数；amount 金额（按货币分别统计）；percent 百分比；usd 美元；tokens 令牌数。
/// </summary>
public enum MetricUnit
{
    Count = 0,
    Amount = 1,
    Percent = 2,
    Usd = 3,
    Tokens = 4,
}

public sealed record MetricDimensionDto(string Key, string Name);

/// <param name="IsRatio">比率类指标（如赢率）：合计是整个区间的比率，不是各天相加。</param>
public sealed record MetricDefinitionDto(
    string Key,
    string ModuleKey,
    string Name,
    string Description,
    MetricUnit Unit,
    bool IsRatio,
    IReadOnlyList<MetricDimensionDto> Dimensions);

/// <summary>
/// 指标查询。日期是空间时区（或 TimeZone 指定的时区）里的日期，From、To 都包含在内；
/// 不传 From、To 时按 Range 推算，默认近 30 天。
/// </summary>
public sealed record MetricQueryRequest
{
    public string Metric { get; init; } = string.Empty;

    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    public AnalyticsRange? Range { get; init; }

    public AnalyticsInterval Interval { get; init; } = AnalyticsInterval.Day;

    /// <summary>
    /// 拆分维度，取值见指标的 Dimensions。
    /// </summary>
    public string? Breakdown { get; init; }

    /// <summary>
    /// 金额类指标的货币；不传时取区间内金额最多的货币。
    /// </summary>
    public string? Currency { get; init; }

    /// <summary>
    /// IANA 时区名；不传时用空间时区。
    /// </summary>
    public string? TimeZone { get; init; }

    /// <summary>
    /// 同时计算上一个等长区间的合计，用于环比。
    /// </summary>
    public bool Compare { get; init; } = true;

    /// <summary>
    /// 拆分时最多列出几组，其余合并为"其他"。
    /// </summary>
    public int Limit { get; init; } = 8;
}

/// <param name="Date">这一段的开始日期（按周统计时是周一，按月统计时是 1 号）。</param>
/// <param name="Value">比率类指标在没有数据的时段为空。</param>
public sealed record MetricPointDto(DateOnly Date, decimal? Value);

public sealed record MetricSeriesDto(string Key, string Label, decimal? Total, IReadOnlyList<MetricPointDto> Points);

/// <param name="Currencies">区间内出现过的货币（按金额从多到少），可以切换查看。</param>
/// <param name="PreviousTotal">上一个等长区间的合计；没有要求对比时为空。</param>
/// <param name="Series">按 Breakdown 拆分后的各组，按合计从大到小。</param>
public sealed record MetricResultDto(
    string Metric,
    string Name,
    MetricUnit Unit,
    bool IsRatio,
    DateOnly From,
    DateOnly To,
    AnalyticsInterval Interval,
    string TimeZone,
    string? Currency,
    IReadOnlyList<string> Currencies,
    decimal? Total,
    decimal? PreviousTotal,
    IReadOnlyList<MetricPointDto> Points,
    string? Breakdown,
    IReadOnlyList<MetricSeriesDto> Series);

public sealed record AnalyticsKpiDto(string Metric, string Name, MetricUnit Unit, bool IsRatio, string? Currency, decimal? Value, decimal? PreviousValue);

public sealed record AnalyticsAmountDto(string Currency, decimal Amount);

public sealed record PipelineStageSummaryDto(Guid StageId, string Name, int Probability, int Deals, IReadOnlyList<AnalyticsAmountDto> Amounts);

public sealed record LowStockItemDto(Guid ProductId, string Sku, string Name, string Unit, decimal Available, decimal ReorderPoint);

public sealed record ProjectHealthDto(
    Guid ProjectId,
    string Name,
    ProjectStatus Status,
    int Percent,
    int OpenTasks,
    int Blocked,
    int Overdue,
    DateOnly? TargetDate,
    bool IsOverdue);

/// <param name="Runs">区间内的执行次数。</param>
/// <param name="CostUsd">区间内的花费（美元）。</param>
public sealed record AgentActivityDto(Guid AgentId, string Name, string Title, int OpenTasks, int Runs, int FailedRuns, decimal CostUsd);

/// <summary>
/// 经营概览：查看者有权限、模块已启用的部分才会出现，其余为空。
/// </summary>
/// <param name="Pipeline">进行中商机按阶段汇总（当前状态，不随区间变化）。</param>
/// <param name="LowStock">可用数量已到补货提醒线的产品（当前状态）。</param>
/// <param name="Projects">进行中的项目及健康度（当前状态）。</param>
/// <param name="Agents">各智能体的未完成任务（当前）以及区间内的执行和花费。</param>
public sealed record AnalyticsOverviewDto(
    DateOnly From,
    DateOnly To,
    string TimeZone,
    IReadOnlyList<AnalyticsKpiDto> Kpis,
    IReadOnlyList<PipelineStageSummaryDto>? Pipeline,
    IReadOnlyList<LowStockItemDto>? LowStock,
    IReadOnlyList<ProjectHealthDto>? Projects,
    IReadOnlyList<AgentActivityDto>? Agents);

public sealed record AnalyticsReportDto(
    Guid Id,
    string Name,
    string Description,
    string Metric,
    string? Breakdown,
    string? Currency,
    AnalyticsRange Range,
    AnalyticsInterval Interval,
    AnalyticsChart Chart,
    int Position,
    ActorDto CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SaveAnalyticsReportRequest
{
    public required string Name { get; init; }

    public string Description { get; init; } = string.Empty;

    public required string Metric { get; init; }

    public string? Breakdown { get; init; }

    public string? Currency { get; init; }

    public AnalyticsRange Range { get; init; } = AnalyticsRange.Last30Days;

    public AnalyticsInterval Interval { get; init; } = AnalyticsInterval.Day;

    public AnalyticsChart Chart { get; init; } = AnalyticsChart.Line;

    /// <summary>
    /// 编辑时带上读取到的 updatedAt，期间被别人改过会返回 409。
    /// </summary>
    public DateTimeOffset? ExpectedUpdatedAt { get; init; }
}

public sealed record ReorderAnalyticsReportsRequest
{
    public required IReadOnlyList<Guid> Ids { get; init; }
}
