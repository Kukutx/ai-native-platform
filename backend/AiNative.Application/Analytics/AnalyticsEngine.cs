using AiNative.Application.Common;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Analytics;
using AiNative.Domain.Analytics;

namespace AiNative.Application.Analytics;

/// <summary>
/// 统计区间（统计时区里的日期，首尾都包含）。
/// </summary>
public readonly record struct AnalyticsPeriod(DateOnly From, DateOnly To)
{
    public int Days => To.DayNumber - From.DayNumber + 1;

    public static AnalyticsPeriod Resolve(AnalyticsRange range, DateOnly today)
    {
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        return range switch
        {
            AnalyticsRange.Last7Days => new(today.AddDays(-6), today),
            AnalyticsRange.Last90Days => new(today.AddDays(-89), today),
            AnalyticsRange.ThisMonth => new(monthStart, today),
            AnalyticsRange.LastMonth => new(monthStart.AddMonths(-1), monthStart.AddDays(-1)),
            AnalyticsRange.ThisQuarter => new(new DateOnly(today.Year, ((today.Month - 1) / 3 * 3) + 1, 1), today),
            AnalyticsRange.ThisYear => new(new DateOnly(today.Year, 1, 1), today),
            AnalyticsRange.Last12Months => new(monthStart.AddMonths(-11), today),
            _ => new(today.AddDays(-29), today),
        };
    }

    /// <summary>
    /// 用来做环比的上一个区间。按月、季、年的快捷区间对比上一个月、季、年的同一段日期
    /// （例如本月 10 月 1–11 日对比 9 月 1–11 日，上月对比再上一个整月）；其他区间对比紧挨着的等长区间。
    /// </summary>
    public AnalyticsPeriod Previous(AnalyticsRange? range)
    {
        var months = range switch
        {
            AnalyticsRange.ThisMonth or AnalyticsRange.LastMonth => 1,
            AnalyticsRange.ThisQuarter => 3,
            AnalyticsRange.ThisYear or AnalyticsRange.Last12Months => 12,
            _ => 0,
        };
        if (months == 0)
        {
            return new(From.AddDays(-Days), From.AddDays(-1));
        }

        // 区间结束在月末时，上一段也结束在月末（9 月 30 日往前一个月是 8 月 31 日）。
        var endsOnMonthEnd = To.AddDays(1).Day == 1;
        var to = endsOnMonthEnd ? new DateOnly(To.Year, To.Month, 1).AddMonths(1 - months).AddDays(-1) : To.AddMonths(-months);
        return new(From.AddMonths(-months), to);
    }

    public static DateOnly BucketOf(DateOnly date, AnalyticsInterval interval) => interval switch
    {
        AnalyticsInterval.Week => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
        AnalyticsInterval.Month => new DateOnly(date.Year, date.Month, 1),
        _ => date,
    };

    public IReadOnlyList<DateOnly> Buckets(AnalyticsInterval interval)
    {
        var buckets = new List<DateOnly>();
        for (var bucket = BucketOf(From, interval); bucket <= To; bucket = Next(bucket, interval))
        {
            buckets.Add(bucket);
        }

        return buckets;
    }

    private static DateOnly Next(DateOnly bucket, AnalyticsInterval interval) => interval switch
    {
        AnalyticsInterval.Week => bucket.AddDays(7),
        AnalyticsInterval.Month => bucket.AddMonths(1),
        _ => bucket.AddDays(1),
    };
}

/// <summary>
/// 指标查询引擎：解析区间与时区、取样、选货币、按天/周/月分桶、拆分取前几组、计算环比。
/// 各指标只负责取样，口径和计算方式在这里保持一致。
/// </summary>
public sealed class AnalyticsEngine(MetricCatalog catalog, TenantCalendar calendar, TimeProvider clock)
{
    public const string OtherGroup = "__other__";

    /// <summary>
    /// 一次查询最多覆盖的天数（按天统计）以及其他粒度下的上限，避免一次算出过多数据点。
    /// </summary>
    public static int MaxDays(AnalyticsInterval interval) => interval switch
    {
        AnalyticsInterval.Day => 366,
        AnalyticsInterval.Week => 731,
        _ => 1_830,
    };

    /// <summary>
    /// 解析统计时区与区间。给了起止日期时 Range 返回空（自定义区间），否则按快捷区间推算。
    /// </summary>
    public async Task<(string TimeZone, AnalyticsPeriod Period, AnalyticsRange? Range)> ResolvePeriodAsync(
        DateOnly? from,
        DateOnly? to,
        AnalyticsRange? range,
        string? timeZone,
        CancellationToken cancellationToken)
    {
        var tenantZone = await calendar.ZoneAsync(cancellationToken).ConfigureAwait(false);
        var zoneId = string.IsNullOrWhiteSpace(timeZone)
            ? tenantZone.Id
            : TimeZones.Normalize(timeZone) ?? throw Invalid("timeZone", "无法识别的时区，请使用 IANA 名称，例如 Asia/Shanghai。");
        var zone = TimeZones.Find(zoneId)!;
        var today = TimeZones.DateIn(clock.GetUtcNow(), zone);
        if (from is not null || to is not null)
        {
            var end = to ?? today;
            var start = from ?? end.AddDays(-29);
            if (start > end)
            {
                throw Invalid("from", "开始日期不能晚于结束日期。");
            }

            return (zoneId, new AnalyticsPeriod(start, end), null);
        }

        var effective = range ?? AnalyticsRange.Last30Days;
        return (zoneId, AnalyticsPeriod.Resolve(effective, today), effective);
    }

    public async Task<MetricResultDto> QueryAsync(MetricQueryRequest request, CancellationToken cancellationToken)
    {
        var metric = await catalog.RequireAsync(request.Metric.Trim(), cancellationToken).ConfigureAwait(false);
        MetricDimension? dimension = null;
        if (!string.IsNullOrWhiteSpace(request.Breakdown))
        {
            var dimensions = await catalog.DimensionsAsync(metric, cancellationToken).ConfigureAwait(false);
            dimension = dimensions.FirstOrDefault(d => d.Key == request.Breakdown.Trim())
                        ?? throw Invalid("breakdown", dimensions.Count == 0
                            ? $"「{metric.Name}」不支持拆分。"
                            : $"「{metric.Name}」可以按这些维度拆分：{string.Join("、", dimensions.Select(d => $"{d.Key}（{d.Name}）"))}。");
        }

        var (zoneId, period, range) = await ResolvePeriodAsync(request.From, request.To, request.Range, request.TimeZone, cancellationToken).ConfigureAwait(false);
        if (period.Days > MaxDays(request.Interval))
        {
            throw Invalid("to", $"按{IntervalName(request.Interval)}统计时区间最长 {MaxDays(request.Interval)} 天，请缩短区间或改用更粗的粒度。");
        }

        var zone = TimeZones.Find(zoneId)!;
        var samples = await SampleAsync(metric, period, zone, zoneId, dimension?.Key, cancellationToken).ConfigureAwait(false);

        string? currency = null;
        IReadOnlyList<string> currencies = [];
        if (metric.Unit == MetricUnit.Amount)
        {
            currencies = [.. samples.Where(s => s.Currency is not null)
                .GroupBy(s => s.Currency!)
                .OrderByDescending(g => g.Sum(s => s.Value)).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key)];
            currency = string.IsNullOrWhiteSpace(request.Currency) ? currencies.FirstOrDefault() : request.Currency.Trim().ToUpperInvariant();
            var selected = currency;
            samples = [.. samples.Where(s => s.Currency == selected)];
        }

        var buckets = period.Buckets(request.Interval);
        var points = Points(samples, buckets, period, request.Interval, metric.IsRatio);
        var series = dimension is null
            ? []
            : await SeriesAsync(metric, dimension, samples, buckets, period, request.Interval, Math.Clamp(request.Limit, 1, 20), cancellationToken).ConfigureAwait(false);

        decimal? previous = null;
        if (request.Compare)
        {
            var before = period.Previous(range);
            var previousSamples = await SampleAsync(metric, before, zone, zoneId, null, cancellationToken).ConfigureAwait(false);
            if (metric.Unit == MetricUnit.Amount)
            {
                previousSamples = [.. previousSamples.Where(s => s.Currency == currency)];
            }

            previous = Total(previousSamples, metric.IsRatio);
        }

        return new MetricResultDto(
            metric.Key,
            metric.Name,
            metric.Unit,
            metric.IsRatio,
            period.From,
            period.To,
            request.Interval,
            zoneId,
            currency,
            currencies,
            Total(samples, metric.IsRatio),
            previous,
            points,
            dimension?.Key,
            series);
    }

    private static async Task<IReadOnlyList<MetricSample>> SampleAsync(
        IPlatformMetric metric,
        AnalyticsPeriod period,
        TimeZoneInfo zone,
        string zoneId,
        string? breakdown,
        CancellationToken cancellationToken)
    {
        var scope = new MetricScope(TimeZones.StartOfDay(period.From, zone), TimeZones.StartOfDay(period.To.AddDays(1), zone), zoneId, breakdown);
        return await metric.SampleAsync(scope, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<MetricSeriesDto>> SeriesAsync(
        IPlatformMetric metric,
        MetricDimension dimension,
        IReadOnlyList<MetricSample> samples,
        IReadOnlyList<DateOnly> buckets,
        AnalyticsPeriod period,
        AnalyticsInterval interval,
        int limit,
        CancellationToken cancellationToken)
    {
        // 比率类按分母（样本量）排序，其他按数值排序；超出部分合并为"其他"。
        var groups = samples.GroupBy(s => s.Group)
            .Select(g => new { g.Key, Samples = g.ToList(), Rank = metric.IsRatio ? g.Sum(s => s.Weight) : g.Sum(s => s.Value) })
            .OrderByDescending(g => g.Rank).ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        var top = groups.Take(limit).ToList();
        var rest = groups.Skip(limit).SelectMany(g => g.Samples).ToList();

        var keys = top.Select(g => g.Key).Where(k => k.Length > 0).ToList();
        var labels = keys.Count == 0
            ? new Dictionary<string, string>()
            : await metric.LabelsAsync(dimension.Key, keys, cancellationToken).ConfigureAwait(false);

        var series = top.Select(g => new MetricSeriesDto(
                g.Key.Length == 0 ? string.Empty : g.Key,
                g.Key.Length == 0 ? dimension.EmptyLabel : labels.GetValueOrDefault(g.Key) ?? "已删除",
                Total(g.Samples, metric.IsRatio),
                Points(g.Samples, buckets, period, interval, metric.IsRatio)))
            .ToList();
        if (rest.Count > 0)
        {
            series.Add(new MetricSeriesDto(OtherGroup, "其他", Total(rest, metric.IsRatio), Points(rest, buckets, period, interval, metric.IsRatio)));
        }

        return series;
    }

    private static List<MetricPointDto> Points(
        IReadOnlyList<MetricSample> samples,
        IReadOnlyList<DateOnly> buckets,
        AnalyticsPeriod period,
        AnalyticsInterval interval,
        bool ratio)
    {
        var byBucket = samples.GroupBy(s => AnalyticsPeriod.BucketOf(s.Date, interval)).ToDictionary(g => g.Key, g => g.ToList());

        // 第一段可能从区间开始之前（例如周一）算起，显示日期取区间开始日，避免看起来越界。
        return [.. buckets.Select(b => new MetricPointDto(
            b < period.From ? period.From : b,
            byBucket.TryGetValue(b, out var items) ? Total(items, ratio) : ratio ? null : 0m))];
    }

    private static decimal? Total(IReadOnlyCollection<MetricSample> samples, bool ratio)
    {
        if (!ratio)
        {
            return samples.Sum(s => s.Value);
        }

        var weight = samples.Sum(s => s.Weight);
        return weight == 0 ? null : Math.Round(samples.Sum(s => s.Value) * 100m / weight, 1, MidpointRounding.AwayFromZero);
    }

    private static string IntervalName(AnalyticsInterval interval) => interval switch
    {
        AnalyticsInterval.Week => "周",
        AnalyticsInterval.Month => "月",
        _ => "天",
    };

    private static ValidationFailedException Invalid(string field, string message)
        => new(new Dictionary<string, string[]> { [field] = [message] });
}
