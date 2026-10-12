using AiNative.Application.Analytics;
using AiNative.Application.Common;
using AiNative.Application.Tenancy;
using AiNative.Domain.Analytics;

namespace AiNative.UnitTests.Application;

public sealed class AnalyticsPeriodTests
{
    private static readonly DateOnly Today = new(2026, 10, 11);

    [Theory]
    [InlineData(AnalyticsRange.Last7Days, "2026-10-05", "2026-10-11")]
    [InlineData(AnalyticsRange.Last30Days, "2026-09-12", "2026-10-11")]
    [InlineData(AnalyticsRange.ThisMonth, "2026-10-01", "2026-10-11")]
    [InlineData(AnalyticsRange.LastMonth, "2026-09-01", "2026-09-30")]
    [InlineData(AnalyticsRange.ThisQuarter, "2026-10-01", "2026-10-11")]
    [InlineData(AnalyticsRange.ThisYear, "2026-01-01", "2026-10-11")]
    [InlineData(AnalyticsRange.Last12Months, "2025-11-01", "2026-10-11")]
    public void Ranges_resolve_against_today(AnalyticsRange range, string from, string to)
        => AnalyticsPeriod.Resolve(range, Today).Should().Be(new AnalyticsPeriod(DateOnly.Parse(from), DateOnly.Parse(to)));

    [Theory]
    [InlineData(AnalyticsRange.ThisMonth, "2026-09-01", "2026-09-11")]
    [InlineData(AnalyticsRange.LastMonth, "2026-08-01", "2026-08-31")]
    [InlineData(AnalyticsRange.ThisQuarter, "2026-07-01", "2026-07-11")]
    [InlineData(AnalyticsRange.ThisYear, "2025-01-01", "2025-10-11")]
    [InlineData(AnalyticsRange.Last7Days, "2026-09-28", "2026-10-04")]
    public void Comparison_period_matches_the_range(AnalyticsRange range, string from, string to)
        => AnalyticsPeriod.Resolve(range, Today).Previous(range).Should().Be(new AnalyticsPeriod(DateOnly.Parse(from), DateOnly.Parse(to)));

    [Fact]
    public void Month_end_compares_with_the_previous_month_end()
    {
        // 3 月是整月，上一段是 2 月整月，而不是 2 月 1 日到 3 月 3 日之类的错位区间。
        var march = AnalyticsPeriod.Resolve(AnalyticsRange.LastMonth, new DateOnly(2026, 4, 15));
        march.Should().Be(new AnalyticsPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)));
        march.Previous(AnalyticsRange.LastMonth).Should().Be(new AnalyticsPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)));

        // 自定义区间对比紧挨着的等长区间。
        new AnalyticsPeriod(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 7)).Previous(null)
            .Should().Be(new AnalyticsPeriod(new DateOnly(2026, 9, 24), new DateOnly(2026, 9, 30)));
    }

    [Fact]
    public void Buckets_start_on_monday_and_the_first_of_month()
    {
        var period = new AnalyticsPeriod(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 14));
        period.Buckets(AnalyticsInterval.Week).Should().Equal(new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 12));
        period.Buckets(AnalyticsInterval.Month).Should().Equal(new DateOnly(2026, 10, 1));
        period.Buckets(AnalyticsInterval.Day).Should().HaveCount(14);
        AnalyticsPeriod.BucketOf(new DateOnly(2026, 10, 11), AnalyticsInterval.Week).Should().Be(new DateOnly(2026, 10, 5));
    }

    [Theory]
    [InlineData("Asia/Shanghai", "Asia/Shanghai")]
    [InlineData(" Europe/Rome ", "Europe/Rome")]
    [InlineData("China Standard Time", "Asia/Shanghai")]
    [InlineData("UTC", "UTC")]
    [InlineData("Mars/Olympus", null)]
    [InlineData("", null)]
    public void Time_zones_are_normalized_to_iana_names(string id, string? expected)
        => TimeZones.Normalize(id).Should().Be(expected);

    [Fact]
    public void Day_boundaries_follow_the_time_zone()
    {
        var shanghai = TimeZones.Find("Asia/Shanghai")!;
        TimeZones.StartOfDay(new DateOnly(2026, 10, 11), shanghai).Should().Be(new DateTimeOffset(2026, 10, 10, 16, 0, 0, TimeSpan.Zero));
        TimeZones.DateIn(new DateTimeOffset(2026, 10, 10, 17, 0, 0, TimeSpan.Zero), shanghai).Should().Be(new DateOnly(2026, 10, 11));

        // 夏令时开始那天 0 点不存在的时区，取当天第一个存在的时刻。
        var santiago = TimeZones.Find("America/Santiago")!;
        var start = TimeZones.StartOfDay(new DateOnly(2026, 9, 6), santiago);
        TimeZones.DateIn(start, santiago).Should().Be(new DateOnly(2026, 9, 6));
        TimeZones.DateIn(start.AddMinutes(-1), santiago).Should().Be(new DateOnly(2026, 9, 5));

        TimeZones.Describe(new DateTimeOffset(2026, 10, 11, 0, 30, 0, TimeSpan.Zero), "Asia/Shanghai")
            .Should().Be("2026-10-11 08:30（星期日，Asia/Shanghai）");
        TimeZones.Describe(new DateTimeOffset(2026, 10, 11, 0, 30, 0, TimeSpan.Zero), null).Should().EndWith("（星期日，UTC）");
    }

    [Fact]
    public void Range_values_use_snake_case_in_json()
        => PlatformJson.Serialize(new[] { AnalyticsRange.Last7Days, AnalyticsRange.ThisQuarter, AnalyticsRange.Last12Months })
            .Should().Be("[\"last7_days\",\"this_quarter\",\"last12_months\"]");
}
