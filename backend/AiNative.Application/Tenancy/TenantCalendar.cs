using AiNative.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Tenancy;

/// <summary>
/// 时区名的规范化与换算。统一用 IANA 名称，因为 PostgreSQL 的 AT TIME ZONE 和三端都认它。
/// </summary>
public static class TimeZones
{
    public const string Utc = "UTC";

    /// <summary>
    /// 规范成 IANA 名称（Windows 时区名会被换成对应的 IANA 名称），无法识别时返回 null。
    /// </summary>
    public static string? Normalize(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(id.Trim(), out var zone))
        {
            return null;
        }

        if (zone.HasIanaId)
        {
            return zone.Id;
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : null;
    }

    public static TimeZoneInfo? Find(string? id)
        => Normalize(id) is { } normalized && TimeZoneInfo.TryFindSystemTimeZoneById(normalized, out var zone) ? zone : null;

    /// <summary>
    /// 某时区里某天开始的时刻（UTC）。个别时区在夏令时切换日没有 0 点，这时取当天第一个存在的时刻。
    /// </summary>
    public static DateTimeOffset StartOfDay(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        for (var i = 0; i < 8 && zone.IsInvalidTime(local); i++)
        {
            local = local.AddMinutes(30);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    public static DateOnly DateIn(DateTimeOffset instant, TimeZoneInfo zone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    /// <summary>
    /// 给模型看的当地时间，例如"2026-10-12 08:30（星期一，Asia/Shanghai）"，让它理解"今天""本月"指哪天。
    /// </summary>
    public static string Describe(DateTimeOffset instant, string? zoneId)
    {
        var id = Normalize(zoneId) ?? Utc;
        var local = TimeZoneInfo.ConvertTime(instant, Find(id) ?? TimeZoneInfo.Utc);
        string[] weekdays = ["星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六"];
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{local:yyyy-MM-dd HH:mm}（{weekdays[(int)local.DayOfWeek]}，{id}）");
    }
}

/// <summary>
/// 当前空间时区下的日期。时区在一次请求内只查一次。
/// </summary>
public sealed class TenantCalendar(IAppDbContext db, ITenantContext tenant, TimeProvider clock)
{
    private (string Id, TimeZoneInfo Zone)? _zone;

    public async Task<(string Id, TimeZoneInfo Zone)> ZoneAsync(CancellationToken cancellationToken)
    {
        if (_zone is { } cached)
        {
            return cached;
        }

        var id = await db.Tenants.AsNoTracking().Where(t => t.Id == tenant.TenantId).Select(t => t.TimeZone)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var zone = TimeZones.Find(id);
        _zone = zone is null ? (TimeZones.Utc, TimeZoneInfo.Utc) : (TimeZones.Normalize(id)!, zone);
        return _zone.Value;
    }

    public async Task<DateOnly> TodayAsync(CancellationToken cancellationToken)
        => TimeZones.DateIn(clock.GetUtcNow(), (await ZoneAsync(cancellationToken).ConfigureAwait(false)).Zone);

    /// <summary>
    /// 空间时区被修改后，让本次请求后续的计算用新时区。
    /// </summary>
    public void Reset() => _zone = null;
}
