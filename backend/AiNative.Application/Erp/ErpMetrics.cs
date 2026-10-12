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

    public override string Description => "按每次发货的时间统计发货金额（分批发货按每批实际发出的数量），客户退货在退货当天冲减；不同货币分开统计。";

    public override string RequiredFeature => ErpFeatures.View;

    public override MetricUnit Unit => MetricUnit.Amount;

    public override IReadOnlyList<MetricDimension> Dimensions { get; } =
    [
        new("company", "客户", EmptyLabel: "未指定客户"),
        new("product", "产品"),
        new("owner", "负责人", EmptyLabel: "未分配负责人"),
    ];

    public override Task<IReadOnlyList<MetricSample>> SampleAsync(MetricScope scope, CancellationToken cancellationToken)
        => ErpSalesRows.Of(db, scope, grossProfit: false).ByDayAsync(scope.TimeZone, cancellationToken);

    public override Task<IReadOnlyDictionary<string, string>> LabelsAsync(string dimension, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
        => ErpSalesRows.LabelsAsync(labels, dimension, keys, cancellationToken);
}

internal sealed class GrossProfitMetric(IAppDbContext db, MetricLabels labels) : PlatformMetric
{
    public override string Key => "erp.gross_profit";

    public override string ModuleKey => ErpModule.ModuleKey;

    public override string Name => "毛利";

    public override string Description => "发货金额减去销售成本（库存按移动加权平均计算的出库成本），按每次发货的时间统计，客户退货在退货当天冲减；成本货币与订单货币不同的明细（暂不做汇率换算）不计入。";

    public override string RequiredFeature => ErpFeatures.View;

    public override MetricUnit Unit => MetricUnit.Amount;

    public override IReadOnlyList<MetricDimension> Dimensions { get; } =
    [
        new("company", "客户", EmptyLabel: "未指定客户"),
        new("product", "产品"),
        new("owner", "负责人", EmptyLabel: "未分配负责人"),
    ];

    public override Task<IReadOnlyList<MetricSample>> SampleAsync(MetricScope scope, CancellationToken cancellationToken)
        => ErpSalesRows.Of(db, scope, grossProfit: true).ByDayAsync(scope.TimeZone, cancellationToken);

    public override Task<IReadOnlyDictionary<string, string>> LabelsAsync(string dimension, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
        => ErpSalesRows.LabelsAsync(labels, dimension, keys, cancellationToken);
}

/// <summary>
/// 发货金额与毛利共用的采样：发货单为正、销售退货单为负，按单据明细拆到产品，客户和负责人取自订单。
/// </summary>
internal static class ErpSalesRows
{
    public static IQueryable<MetricRow> Of(IAppDbContext db, MetricScope scope, bool grossProfit)
    {
        var lines = from d in db.ErpStockDocuments
                    where (d.Kind == ErpStockDocumentKind.Shipment || d.Kind == ErpStockDocumentKind.SalesReturn) && d.CreatedAt >= scope.From && d.CreatedAt < scope.To
                    join o in db.ErpSalesOrders on d.SalesOrderId equals o.Id
                    from l in d.Lines
                    where !grossProfit || l.CostCurrency == d.Currency
                    select new
                    {
                        d.CreatedAt,
                        d.Currency,
                        o.CompanyId,
                        o.OwnerUserId,
                        l.ProductId,
                        Value = (d.Kind == ErpStockDocumentKind.Shipment ? 1 : -1) * (grossProfit ? l.Amount - l.CostAmount : l.Amount),
                    };
        return scope.Breakdown switch
        {
            "company" => lines.Select(x => new MetricRow { At = x.CreatedAt, Group = x.CompanyId.ToString()!, Currency = x.Currency, Value = x.Value }),
            "owner" => lines.Select(x => new MetricRow { At = x.CreatedAt, Group = x.OwnerUserId.ToString()!, Currency = x.Currency, Value = x.Value }),
            "product" => lines.Select(x => new MetricRow { At = x.CreatedAt, Group = x.ProductId.ToString(), Currency = x.Currency, Value = x.Value }),
            _ => lines.Select(x => new MetricRow { At = x.CreatedAt, Currency = x.Currency, Value = x.Value }),
        };
    }

    public static Task<IReadOnlyDictionary<string, string>> LabelsAsync(MetricLabels labels, string dimension, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
        => dimension switch
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

    public override string Description => "按每次到货的时间统计入库的采购金额（分批到货按每批实际收到的数量），退货给供应商在退货当天冲减；不同货币分开统计。";

    public override string RequiredFeature => ErpFeatures.View;

    public override MetricUnit Unit => MetricUnit.Amount;

    public override IReadOnlyList<MetricDimension> Dimensions { get; } =
    [
        new("supplier", "供应商", EmptyLabel: "未指定供应商"),
        new("product", "产品"),
    ];

    public override Task<IReadOnlyList<MetricSample>> SampleAsync(MetricScope scope, CancellationToken cancellationToken)
    {
        var lines = from d in db.ErpStockDocuments
                    where (d.Kind == ErpStockDocumentKind.Receipt || d.Kind == ErpStockDocumentKind.PurchaseReturn) && d.CreatedAt >= scope.From && d.CreatedAt < scope.To
                    join o in db.ErpPurchaseOrders on d.PurchaseOrderId equals o.Id
                    from l in d.Lines
                    select new { d.CreatedAt, d.Currency, o.SupplierId, l.ProductId, Value = (d.Kind == ErpStockDocumentKind.Receipt ? 1 : -1) * l.Amount };
        var rows = scope.Breakdown switch
        {
            "supplier" => lines.Select(x => new MetricRow { At = x.CreatedAt, Group = x.SupplierId.ToString()!, Currency = x.Currency, Value = x.Value }),
            "product" => lines.Select(x => new MetricRow { At = x.CreatedAt, Group = x.ProductId.ToString(), Currency = x.Currency, Value = x.Value }),
            _ => lines.Select(x => new MetricRow { At = x.CreatedAt, Currency = x.Currency, Value = x.Value }),
        };
        return rows.ByDayAsync(scope.TimeZone, cancellationToken);
    }

    public override Task<IReadOnlyDictionary<string, string>> LabelsAsync(string dimension, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
        => dimension == "product" ? labels.ProductsAsync(keys, cancellationToken) : labels.CompaniesAsync(keys, cancellationToken);
}
