using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Agents;
using AiNative.Contracts.Erp;
using AiNative.Domain.Erp;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Erp;

[RequireFeature(ErpFeatures.View)]
public sealed record GetErpSummaryQuery : IQuery<ErpSummaryDto>;

internal sealed class ErpSummaryHandler(IAppDbContext db, TimeProvider clock) : IRequestHandler<GetErpSummaryQuery, ErpSummaryDto>
{
    public async Task<ErpSummaryDto> HandleAsync(GetErpSummaryQuery request, CancellationToken cancellationToken)
    {
        var monthStart = BillingPeriod.MonthStart(clock.GetUtcNow());

        // 本月发货、到货按单据统计（分批发货按每批的时间），退货当月冲减；毛利只算成本货币与订单货币相同的明细。
        var documents = await db.ErpStockDocuments.AsNoTracking()
            .Where(d => d.CreatedAt >= monthStart)
            .SelectMany(d => d.Lines, (d, l) => new { d.Kind, d.Currency, l.Amount, l.CostAmount, Matched = l.CostCurrency == d.Currency })
            .GroupBy(x => new { x.Kind, x.Currency, x.Matched })
            .Select(g => new { g.Key.Kind, g.Key.Currency, g.Key.Matched, Amount = g.Sum(x => x.Amount), Cost = g.Sum(x => x.CostAmount) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var sales = documents.Where(d => d.Kind is ErpStockDocumentKind.Shipment or ErpStockDocumentKind.SalesReturn).ToList();
        var shipped = Totals(sales.Select(d => (d.Currency, ErpStockDocument.Sign(d.Kind) * d.Amount)));
        var grossProfit = Totals(sales.Where(d => d.Matched).Select(d => (d.Currency, ErpStockDocument.Sign(d.Kind) * (d.Amount - d.Cost))));
        var received = Totals(documents.Where(d => d.Kind is ErpStockDocumentKind.Receipt or ErpStockDocumentKind.PurchaseReturn)
            .Select(d => (d.Currency, ErpStockDocument.Sign(d.Kind) * d.Amount)));
        var salesCounts = await db.ErpSalesOrders.AsNoTracking()
            .Where(o => o.Status == ErpSalesOrderStatus.Draft || o.Status == ErpSalesOrderStatus.Confirmed || o.Status == ErpSalesOrderStatus.PartiallyShipped)
            .GroupBy(o => o.Status == ErpSalesOrderStatus.Draft)
            .Select(g => new { Draft = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // 待发货金额只算还没发的部分，按行四舍五入到分，与整行金额的算法一致。
        var toShipTotals = await db.ErpSalesOrders.AsNoTracking()
            .Where(o => o.Status == ErpSalesOrderStatus.Confirmed || o.Status == ErpSalesOrderStatus.PartiallyShipped)
            .SelectMany(o => o.Lines, (o, l) => new { o.Currency, Value = Math.Round((l.Quantity - l.Delivered) * l.UnitPrice, 2) })
            .GroupBy(x => x.Currency)
            .Select(g => new { Currency = g.Key, Total = g.Sum(x => x.Value) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var openPurchases = await db.ErpPurchaseOrders.AsNoTracking()
            .CountAsync(
                o => o.Status == ErpPurchaseOrderStatus.Draft || o.Status == ErpPurchaseOrderStatus.Ordered || o.Status == ErpPurchaseOrderStatus.PartiallyReceived,
                cancellationToken)
            .ConfigureAwait(false);
        var activeProducts = await db.ErpProducts.AsNoTracking().CountAsync(p => p.IsActive, cancellationToken).ConfigureAwait(false);
        var lowStock = await db.ErpProducts.AsNoTracking()
            .CountAsync(p => p.IsActive && p.TrackInventory && p.ReorderPoint != null
                && (db.ErpStockLevels.Where(l => l.ProductId == p.Id).Sum(l => (decimal?)(l.OnHand - l.Reserved)) ?? 0) <= p.ReorderPoint, cancellationToken)
            .ConfigureAwait(false);
        var stockValue = await db.ErpStockLevels.AsNoTracking()
            .Where(l => l.OnHand != 0)
            .Join(db.ErpProducts.Where(p => p.TrackInventory), l => l.ProductId, p => p.Id, (l, p) => new { p.Currency, Value = l.OnHand * l.AverageCost })
            .GroupBy(x => x.Currency)
            .Select(g => new { Currency = g.Key, Value = g.Sum(x => x.Value) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new ErpSummaryDto(
            shipped,
            grossProfit,
            received,
            salesCounts.Where(c => c.Draft).Sum(c => c.Count),
            salesCounts.Where(c => !c.Draft).Sum(c => c.Count),
            Totals(toShipTotals.Select(c => (c.Currency, c.Total))),
            openPurchases,
            activeProducts,
            lowStock,
            Totals(stockValue.Select(v => (v.Currency, Math.Round(v.Value, 2, MidpointRounding.AwayFromZero)))));
    }

    /// <summary>
    /// 按货币合计并排序；货币列是定长字符，去掉可能的空白。
    /// </summary>
    private static IReadOnlyList<ErpAmountDto> Totals(IEnumerable<(string Currency, decimal Amount)> amounts)
        =>
        [
            .. amounts.GroupBy(a => a.Currency.Trim(), StringComparer.Ordinal)
                .Select(g => new ErpAmountDto(g.Key, g.Sum(a => a.Amount)))
                .OrderBy(a => a.Currency, StringComparer.Ordinal),
        ];
}
