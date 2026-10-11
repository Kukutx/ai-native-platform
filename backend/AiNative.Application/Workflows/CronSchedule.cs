namespace AiNative.Application.Workflows;

/// <summary>
/// 5 段 cron 表达式（分 时 日 月 周），支持 * , - / 和英文月份、星期缩写（JAN、MON-FRI）。
/// 日与周同时限定时按惯例取"或"。时区按 IANA 名称解析；夏令时跳过的时刻顺延到跳变之后，重复的时刻只执行第一次。
/// </summary>
public sealed class CronSchedule
{
    private static readonly string[] MonthNames = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];
    private static readonly string[] DayNames = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    // 最多向后搜索的天数：覆盖 2 月 29 日这类每 4 年一次的规则，还留有余量。
    private const int MaxSearchDays = 366 * 8;

    private readonly bool[] _minutes;
    private readonly bool[] _hours;
    private readonly bool[] _days;
    private readonly bool[] _months;
    private readonly bool[] _weekdays;
    private readonly bool _daysRestricted;
    private readonly bool _weekdaysRestricted;

    private CronSchedule(string expression, bool[] minutes, bool[] hours, bool[] days, bool[] months, bool[] weekdays, bool daysRestricted, bool weekdaysRestricted)
    {
        Expression = expression;
        _minutes = minutes;
        _hours = hours;
        _days = days;
        _months = months;
        _weekdays = weekdays;
        _daysRestricted = daysRestricted;
        _weekdaysRestricted = weekdaysRestricted;
    }

    public string Expression { get; }

    public static CronSchedule Parse(string expression)
    {
        var fields = (expression ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 5)
        {
            throw new FormatException("cron 表达式需要 5 段：分 时 日 月 周，例如 \"0 9 * * MON-FRI\"。");
        }

        var weekdays = ParseField(fields[4], 0, 7, DayNames, "周");
        weekdays[0] |= weekdays[7];
        return new CronSchedule(
            string.Join(' ', fields),
            ParseField(fields[0], 0, 59, null, "分"),
            ParseField(fields[1], 0, 23, null, "时"),
            ParseField(fields[2], 1, 31, null, "日"),
            ParseField(fields[3], 1, 12, MonthNames, "月"),
            weekdays[..7],
            !fields[2].StartsWith('*'),
            !fields[4].StartsWith('*'));
    }

    public static bool TryParse(string expression, out CronSchedule? schedule, out string? error)
    {
        try
        {
            schedule = Parse(expression);
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            schedule = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 解析时区；找不到时返回 null。
    /// </summary>
    public static TimeZoneInfo? FindTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Equals("UTC", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>
    /// 严格晚于 <paramref name="after"/> 的下一次执行时间（UTC）；规则永远不会命中时返回 null（例如 2 月 30 日）。
    /// </summary>
    public DateTimeOffset? GetNextOccurrence(DateTimeOffset after, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        var firstDay = local.Date;
        var startMinuteOfFirstDay = local.Hour * 60 + local.Minute;

        for (var offset = 0; offset < MaxSearchDays; offset++)
        {
            var day = firstDay.AddDays(offset);
            if (!_months[day.Month] || !DayMatches(day))
            {
                continue;
            }

            for (var hour = 0; hour < 24; hour++)
            {
                if (!_hours[hour])
                {
                    continue;
                }

                for (var minute = 0; minute < 60; minute++)
                {
                    if (!_minutes[minute] || (offset == 0 && hour * 60 + minute < startMinuteOfFirstDay))
                    {
                        continue;
                    }

                    var candidate = ToUtc(day.AddHours(hour).AddMinutes(minute), zone);
                    if (candidate > after)
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }

    private bool DayMatches(DateTime day)
    {
        var dom = _days[day.Day];
        var dow = _weekdays[(int)day.DayOfWeek];
        if (_daysRestricted && _weekdaysRestricted)
        {
            return dom || dow;
        }

        return dom && dow;
    }

    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            // 夏令时开始时跳过的本地时刻：顺延到跳变后的第一个有效分钟。
            var shifted = local;
            do
            {
                shifted = shifted.AddMinutes(1);
            }
            while (zone.IsInvalidTime(shifted));

            return new DateTimeOffset(shifted, zone.GetUtcOffset(shifted)).ToUniversalTime();
        }

        // 夏令时结束时重复的本地时刻取第一次出现（偏移量较大的那个）。
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static bool[] ParseField(string field, int min, int max, string[]? names, string label)
    {
        var allowed = new bool[max + 1];
        foreach (var part in field.Split(','))
        {
            if (part.Length == 0)
            {
                throw new FormatException($"cron 的「{label}」段有多余的逗号。");
            }

            var rangeText = part;
            var step = 1;
            var slash = part.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                rangeText = part[..slash];
                if (!int.TryParse(part[(slash + 1)..], out step) || step <= 0)
                {
                    throw new FormatException($"cron 的「{label}」段步长 {part[(slash + 1)..]} 不合法。");
                }
            }

            int from;
            int to;
            if (rangeText == "*")
            {
                from = min;
                to = max;
            }
            else
            {
                var dash = rangeText.IndexOf('-', StringComparison.Ordinal);
                from = ParseValue(dash >= 0 ? rangeText[..dash] : rangeText, min, max, names, label);
                to = dash >= 0 ? ParseValue(rangeText[(dash + 1)..], min, max, names, label) : (slash >= 0 ? max : from);
                if (from > to)
                {
                    throw new FormatException($"cron 的「{label}」段范围 {rangeText} 起点大于终点。");
                }
            }

            for (var value = from; value <= to; value += step)
            {
                allowed[value] = true;
            }
        }

        return allowed;
    }

    private static int ParseValue(string text, int min, int max, string[]? names, string label)
    {
        if (names is not null)
        {
            var index = Array.FindIndex(names, n => n.Equals(text, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                return index + (names == MonthNames ? 1 : 0);
            }
        }

        if (!int.TryParse(text, out var value) || value < min || value > max)
        {
            throw new FormatException($"cron 的「{label}」段取值 {text} 超出范围（{min}-{max}）。");
        }

        return value;
    }
}
