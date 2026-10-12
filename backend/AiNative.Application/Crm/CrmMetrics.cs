using AiNative.Application.Abstractions;
using AiNative.Application.Analytics;
using AiNative.Contracts.Analytics;
using AiNative.Domain.Common;
using AiNative.Domain.Crm;

namespace AiNative.Application.Crm;

internal static class CrmDimensions
{
    public static readonly MetricDimension Owner = new("owner", "负责人", EmptyLabel: "未分配负责人");

    public static readonly MetricDimension Company = new("company", "客户", EmptyLabel: "个人客户");
}

internal sealed class DealsCreatedMetric(IAppDbContext db, MetricLabels labels) : PlatformMetric
{
    public override string Key => "crm.deals_created";

    public override string ModuleKey => CrmModule.ModuleKey;

    public override string Name => "新增商机";

    public override string Description => "按创建时间统计新建的商机数量（不论后来是否赢单）。";

    public override string RequiredFeature => CrmFeatures.View;

    public override MetricUnit Unit => MetricUnit.Count;

    public override IReadOnlyList<MetricDimension> Dimensions { get; } = [CrmDimensions.Owner, CrmDimensions.Company];

    public override Task<IReadOnlyList<MetricSample>> SampleAsync(MetricScope scope, CancellationToken cancellationToken)
    {
        var deals = db.CrmDeals.AsQueryable().Where(d => d.CreatedAt >= scope.From && d.CreatedAt < scope.To);
        var rows = scope.Breakdown switch
        {
            "owner" => deals.Select(d => new MetricRow { At = d.CreatedAt, Group = d.OwnerUserId.ToString()!, Value = 1 }),
            "company" => deals.Select(d => new MetricRow { At = d.CreatedAt, Group = d.CompanyId.ToString()!, Value = 1 }),
            _ => deals.Select(d => new MetricRow { At = d.CreatedAt, Value = 1 }),
        };
        return rows.ByDayAsync(scope.TimeZone, cancellationToken);
    }

    public override Task<IReadOnlyDictionary<string, string>> LabelsAsync(string dimension, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
        => dimension == "company" ? labels.CompaniesAsync(keys, cancellationToken) : labels.UsersAsync(keys, cancellationToken);
}

internal sealed class DealsWonMetric(IAppDbContext db, MetricLabels labels) : PlatformMetric
{
    public override string Key => "crm.deals_won";

    public override string ModuleKey => CrmModule.ModuleKey;

    public override string Name => "赢单金额";

    public override string Description => "按赢单时间统计赢单商机的金额，不同货币分开统计；没填金额的商机不计入。";

    public override string RequiredFeature => CrmFeatures.View;

    public override MetricUnit Unit => MetricUnit.Amount;

    public override IReadOnlyList<MetricDimension> Dimensions { get; } = [CrmDimensions.Owner, CrmDimensions.Company];

    public override Task<IReadOnlyList<MetricSample>> SampleAsync(MetricScope scope, CancellationToken cancellationToken)
    {
        var deals = db.CrmDeals.AsQueryable()
            .Where(d => d.Status == CrmDealStatus.Won && d.Amount != null && d.ClosedAt >= scope.From && d.ClosedAt < scope.To);
        var rows = scope.Breakdown switch
        {
            "owner" => deals.Select(d => new MetricRow { At = d.ClosedAt!.Value, Group = d.OwnerUserId.ToString()!, Currency = d.Currency, Value = d.Amount!.Value }),
            "company" => deals.Select(d => new MetricRow { At = d.ClosedAt!.Value, Group = d.CompanyId.ToString()!, Currency = d.Currency, Value = d.Amount!.Value }),
            _ => deals.Select(d => new MetricRow { At = d.ClosedAt!.Value, Currency = d.Currency, Value = d.Amount!.Value }),
        };
        return rows.ByDayAsync(scope.TimeZone, cancellationToken);
    }

    public override Task<IReadOnlyDictionary<string, string>> LabelsAsync(string dimension, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
        => dimension == "company" ? labels.CompaniesAsync(keys, cancellationToken) : labels.UsersAsync(keys, cancellationToken);
}

internal sealed class DealsWonCountMetric(IAppDbContext db, MetricLabels labels) : PlatformMetric
{
    public override string Key => "crm.deals_won_count";

    public override string ModuleKey => CrmModule.ModuleKey;

    public override string Name => "赢单数";

    public override string Description => "按赢单时间统计赢单的商机数量（包括没填金额的）。";

    public override string RequiredFeature => CrmFeatures.View;

    public override MetricUnit Unit => MetricUnit.Count;

    public override IReadOnlyList<MetricDimension> Dimensions { get; } = [CrmDimensions.Owner, CrmDimensions.Company];

    public override Task<IReadOnlyList<MetricSample>> SampleAsync(MetricScope scope, CancellationToken cancellationToken)
    {
        var deals = db.CrmDeals.AsQueryable().Where(d => d.Status == CrmDealStatus.Won && d.ClosedAt >= scope.From && d.ClosedAt < scope.To);
        var rows = scope.Breakdown switch
        {
            "owner" => deals.Select(d => new MetricRow { At = d.ClosedAt!.Value, Group = d.OwnerUserId.ToString()!, Value = 1 }),
            "company" => deals.Select(d => new MetricRow { At = d.ClosedAt!.Value, Group = d.CompanyId.ToString()!, Value = 1 }),
            _ => deals.Select(d => new MetricRow { At = d.ClosedAt!.Value, Value = 1 }),
        };
        return rows.ByDayAsync(scope.TimeZone, cancellationToken);
    }

    public override Task<IReadOnlyDictionary<string, string>> LabelsAsync(string dimension, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
        => dimension == "company" ? labels.CompaniesAsync(keys, cancellationToken) : labels.UsersAsync(keys, cancellationToken);
}

internal sealed class WinRateMetric(IAppDbContext db, MetricLabels labels) : PlatformMetric
{
    public override string Key => "crm.win_rate";

    public override string ModuleKey => CrmModule.ModuleKey;

    public override string Name => "赢率";

    public override string Description => "区间内结束的商机中赢单的比例：赢单数 ÷（赢单数 + 输单数），按结束时间统计。";

    public override string RequiredFeature => CrmFeatures.View;

    public override MetricUnit Unit => MetricUnit.Percent;

    public override bool IsRatio => true;

    public override IReadOnlyList<MetricDimension> Dimensions { get; } = [CrmDimensions.Owner];

    public override Task<IReadOnlyList<MetricSample>> SampleAsync(MetricScope scope, CancellationToken cancellationToken)
    {
        var deals = db.CrmDeals.AsQueryable().Where(d => d.Status != CrmDealStatus.Open && d.ClosedAt >= scope.From && d.ClosedAt < scope.To);
        var rows = scope.Breakdown == "owner"
            ? deals.Select(d => new MetricRow { At = d.ClosedAt!.Value, Group = d.OwnerUserId.ToString()!, Value = d.Status == CrmDealStatus.Won ? 1 : 0, Weight = 1 })
            : deals.Select(d => new MetricRow { At = d.ClosedAt!.Value, Value = d.Status == CrmDealStatus.Won ? 1 : 0, Weight = 1 });
        return rows.ByDayAsync(scope.TimeZone, cancellationToken);
    }

    public override Task<IReadOnlyDictionary<string, string>> LabelsAsync(string dimension, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
        => labels.UsersAsync(keys, cancellationToken);
}

internal sealed class ActivitiesMetric(IAppDbContext db, MetricLabels labels) : PlatformMetric
{
    private static readonly IReadOnlyDictionary<string, string> KindNames = new Dictionary<string, string>
    {
        [nameof(CrmActivityKind.Note)] = "备注",
        [nameof(CrmActivityKind.Call)] = "电话",
        [nameof(CrmActivityKind.Meeting)] = "会议",
        [nameof(CrmActivityKind.Email)] = "邮件",
    };

    public override string Key => "crm.activities";

    public override string ModuleKey => CrmModule.ModuleKey;

    public override string Name => "跟进次数";

    public override string Description => "按发生时间统计电话、会议、邮件和备注等跟进记录，不含推进商机时自动生成的阶段变化记录。";

    public override string RequiredFeature => CrmFeatures.View;

    public override MetricUnit Unit => MetricUnit.Count;

    public override IReadOnlyList<MetricDimension> Dimensions { get; } =
    [
        new("kind", "类型"),
        new("author", "记录人"),
    ];

    public override Task<IReadOnlyList<MetricSample>> SampleAsync(MetricScope scope, CancellationToken cancellationToken)
    {
        var activities = db.CrmActivities.AsQueryable()
            .Where(a => a.Kind != CrmActivityKind.StageChange && a.OccurredAt >= scope.From && a.OccurredAt < scope.To);
        var rows = scope.Breakdown switch
        {
            "kind" => activities.Select(a => new MetricRow { At = a.OccurredAt, Group = a.Kind.ToString(), Value = 1 }),
            "author" => activities.Select(a => new MetricRow { At = a.OccurredAt, Group = a.Author.Kind == ActorKind.System ? string.Empty : a.Author.Id.ToString()!, Value = 1 }),
            _ => activities.Select(a => new MetricRow { At = a.OccurredAt, Value = 1 }),
        };
        return rows.ByDayAsync(scope.TimeZone, cancellationToken);
    }

    public override Task<IReadOnlyDictionary<string, string>> LabelsAsync(string dimension, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
        => dimension == "kind" ? Fixed(KindNames) : labels.ActorsAsync(keys, cancellationToken);
}
