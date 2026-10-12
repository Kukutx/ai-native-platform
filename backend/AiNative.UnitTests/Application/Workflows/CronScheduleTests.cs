using AiNative.Application.Workflows;

namespace AiNative.UnitTests.Application.Workflows;

public sealed class CronScheduleTests
{
    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("*/15 * * * *", "2026-10-11T08:07:00Z", "2026-10-11T08:15:00Z")]
    [InlineData("0 9 * * MON-FRI", "2026-10-09T09:00:00Z", "2026-10-12T09:00:00Z")] // 周五 9 点之后 → 下周一
    [InlineData("30 18 1 * *", "2026-10-11T00:00:00Z", "2026-11-01T18:30:00Z")]
    [InlineData("0 0 29 2 *", "2026-03-01T00:00:00Z", "2028-02-29T00:00:00Z")]
    [InlineData("0 12 * JAN,JUL *", "2026-10-11T00:00:00Z", "2027-01-01T12:00:00Z")]
    [InlineData("0 8 15 * SUN", "2026-10-11T09:00:00Z", "2026-10-15T08:00:00Z")] // 日与周同时限定取"或"
    public void Computes_the_next_occurrence_in_utc(string cron, string after, string expected)
        => CronSchedule.Parse(cron).GetNextOccurrence(DateTimeOffset.Parse(after), TimeZoneInfo.Utc)
            .Should().Be(DateTimeOffset.Parse(expected));

    [Fact]
    public void Next_occurrence_is_strictly_after_the_given_instant()
    {
        var schedule = CronSchedule.Parse("0 9 * * *");
        schedule.GetNextOccurrence(Utc(2026, 10, 11, 9, 0), TimeZoneInfo.Utc).Should().Be(Utc(2026, 10, 12, 9, 0));
        schedule.GetNextOccurrence(Utc(2026, 10, 11, 8, 59), TimeZoneInfo.Utc).Should().Be(Utc(2026, 10, 11, 9, 0));
    }

    [Fact]
    public void Uses_the_configured_time_zone()
    {
        var shanghai = TimeZoneInfo.CreateCustomTimeZone("test/+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");

        // 北京时间每天 9 点 = UTC 1 点。
        CronSchedule.Parse("0 9 * * *").GetNextOccurrence(Utc(2026, 10, 11, 2, 0), shanghai).Should().Be(Utc(2026, 10, 12, 1, 0));
    }

    [Fact]
    public void Skipped_local_times_move_past_the_daylight_saving_gap_and_repeated_ones_run_once()
    {
        // 自定义时区：基准 UTC+1，3 月最后一个周日 2 点拨快到 3 点，10 月最后一个周日 3 点拨回 2 点（与欧洲中部时间相同）。
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2000, 1, 1),
            new DateTime(2099, 12, 31),
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday));
        var zone = TimeZoneInfo.CreateCustomTimeZone("test/cet", TimeSpan.FromHours(1), "CET", "CET", "CEST", [rule]);
        var schedule = CronSchedule.Parse("30 2 * * *");

        // 2026-03-29 本地 2:30 不存在 → 顺延到 3:00 本地（UTC+2）= UTC 1:00。
        schedule.GetNextOccurrence(Utc(2026, 3, 28, 12, 0), zone).Should().Be(Utc(2026, 3, 29, 1, 0));

        // 2026-10-25 本地 2:30 出现两次 → 只在第一次（UTC+2）执行 = UTC 0:30，下一次是第二天。
        var first = schedule.GetNextOccurrence(Utc(2026, 10, 24, 12, 0), zone);
        first.Should().Be(Utc(2026, 10, 25, 0, 30));
        schedule.GetNextOccurrence(first!.Value, zone).Should().Be(Utc(2026, 10, 26, 1, 30));
    }

    [Fact]
    public void Impossible_dates_never_occur()
        => CronSchedule.Parse("0 0 30 2 *").GetNextOccurrence(Utc(2026, 1, 1, 0, 0), TimeZoneInfo.Utc).Should().BeNull();

    [Theory]
    [InlineData("* * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("*/0 * * * *")]
    [InlineData("5-1 * * * *")]
    [InlineData("* * * FOO *")]
    public void Rejects_invalid_expressions(string cron)
        => CronSchedule.TryParse(cron, out _, out var error).Should().BeFalse(error);

    [Fact]
    public void Seven_means_sunday()
        => CronSchedule.Parse("0 10 * * 7").GetNextOccurrence(Utc(2026, 10, 12, 0, 0), TimeZoneInfo.Utc).Should().Be(Utc(2026, 10, 18, 10, 0));
}
