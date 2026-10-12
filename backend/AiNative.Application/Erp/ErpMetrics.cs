using AiNative.Application.Abstractions;
using AiNative.Application.Analytics;
using AiNative.Contracts.Analytics;
using AiNative.Domain.Erp;

namespace AiNative.Application.Erp;

internal sealed class SalesShippedMetric(IAppDbContext db, MetricLabels labels) : PlatformMetric
{
    public override string Key => "erp.sales_shipped";

    public override string ModuleKey => ErpModule.ModuleKey;

    public override string Name => "发货金额";

    public override string Description => "按发货时间统计已发货销售订单的金额，不同货币分开统计；按产品拆分时是各产品明细行的金额。";

    public override string RequiredFeature => ErpFeatures.View;

    public override MetricUnit Unit => MetricUnit.Amount;

    public override IReadOnlyList<MetricDimension> Dimensions { get; } =
    [
        new("company", "客户", EmptyLabel: "未指定客户"),
        new("product", "产品"),
        new("owner", "负责人", EmptyLabel: "未分配负责人"),
    ];

    public override Task<IReadOnlyList<MetricSample>> SampleAsync(MetricScope scope, CancellationToken cancellationToken)
    {
        var orders = db.ErpSalesOrders.AsQueryable()
            .Where(o => o.Status == ErpSalesOrderStatus.Shipped && o.ShippedAt >= scope.From && o.ShippedAt < scope.To);
        var rows = scope.Breakdown switch
        {
            "company" => orders.Select(o => new MetricRow { At = o.ShippedAt!.Value, Group = o.CompanyId.ToString()!, Currency = o.Currency, Value = o.Total }),
            "owner" => orders.Select(o => new MetricRow { At = o.ShippedAt!.Value, Group = o.OwnerUserId.ToString()!, Currency = o.Currency, Value = o.Total }),
            "product" => orders.SelectMany(o => o.Lines, (o, l) => new MetricRow { At = o.ShippedAt!.Value, Group = l.ProductId.ToString(), Currency = o.Currency, Value = l.Amount }),
            _ => orders.Select(o => new MetricRow { At = o.ShippedAt!.Value, Currency = o.Currency, Value = o.Total }),
        };
        return rows.ByDayAsync(scope.TimeZone, cancellationToken);
    }

    public override Task<IReadOnlyDictionary<string, string>> LabelsAsync(string dimension, IReadOnlyCollection<string> keys, CancellationToken cancellationToken) => dimension switch
    {
        "company" => labels.CompaniesAsync(keys, cancellationToken),
        "product" => labels.ProductsAsync(keys, cancellationToken),
        _ => labels.UsersAsync(keys, cancellationToken),
    };
}

internal sealed class PurchasesReceivedMetric(IAppDbContext db, MetricLabels labels) : PlatformMetric
{
    public override string Key => "erp.purchases_received";

    public override string ModuleKey => ErpModule.ModuleKey;

    public override string Name => "采购入库金额";

    public override string Description => "按收货时间统计已收货采购订单的金额，不同货币分开统计；按产品拆分时是各产品明细行的金额。";

    public override string RequiredFeature => ErpFeatures.View;

    public override MetricUnit Unit => MetricUnit.Amount;

    public override IReadOnlyList<MetricDimension> Dimensions { get; } =
    [
        new("supplier", "供应商", EmptyLabel: "未指定供应商"),
        new("product", "产品"),
    ];

    public override Task<IReadOnlyList<MetricSample>> SampleAsync(MetricScope scope, CancellationToken cancellationToken)
    {
        var orders = db.ErpPurchaseOrders.AsQueryable()
            .Where(o => o.Status == ErpPurchaseOrderStatus.Received && o.ReceivedAt >= scope.From && o.ReceivedAt < scope.To);
        var rows = scope.Breakdown switch
        {
            "supplier" => orders.Select(o => new MetricRow { At = o.ReceivedAt!.Value, Group = o.SupplierId.ToString()!, Currency = o.Currency, Value = o.Total }),
            "product" => orders.SelectMany(o => o.Lines, (o, l) => new MetricRow { At = o.ReceivedAt!.Value, Group = l.ProductId.ToString(), Currency = o.Currency, Value = l.Amount }),
            _ => orders.Select(o => new MetricRow { At = o.ReceivedAt!.Value, Currency = o.Currency, Value = o.Total }),
        };
        return rows.ByDayAsync(scope.TimeZone, cancellationToken);
    }

    public override Task<IReadOnlyDictionary<string, string>> LabelsAsync(string dimension, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
        => dimension == "product" ? labels.ProductsAsync(keys, cancellationToken) : labels.CompaniesAsync(keys, cancellationToken);
}
