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
        var shipped = await db.ErpSalesOrders.AsNoTracking()
            .Where(o => o.Status == ErpSalesOrderStatus.Shipped && o.ShippedAt >= monthStart)
            .GroupBy(o => o.Currency).Select(g => new ErpAmountDto(g.Key, g.Sum(o => o.Total)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var received = await db.ErpPurchaseOrders.AsNoTracking()
            .Where(o => o.Status == ErpPurchaseOrderStatus.Received && o.ReceivedAt >= monthStart)
            .GroupBy(o => o.Currency).Select(g => new ErpAmountDto(g.Key, g.Sum(o => o.Total)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var salesCounts = await db.ErpSalesOrders.AsNoTracking()
            .Where(o => o.Status == ErpSalesOrderStatus.Draft || o.Status == ErpSalesOrderStatus.Confirmed)
            .GroupBy(o => new { o.Status, o.Currency })
            .Select(g => new { g.Key.Status, g.Key.Currency, Count = g.Count(), Total = g.Sum(o => o.Total) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var openPurchases = await db.ErpPurchaseOrders.AsNoTracking()
            .CountAsync(o => o.Status == ErpPurchaseOrderStatus.Draft || o.Status == ErpPurchaseOrderStatus.Ordered, cancellationToken).ConfigureAwait(false);
        var activeProducts = await db.ErpProducts.AsNoTracking().CountAsync(p => p.IsActive, cancellationToken).ConfigureAwait(false);
        var lowStock = await db.ErpProducts.AsNoTracking()
            .CountAsync(p => p.IsActive && p.TrackInventory && p.ReorderPoint != null
                && (db.ErpStockLevels.Where(l => l.ProductId == p.Id).Sum(l => (decimal?)(l.OnHand - l.Reserved)) ?? 0) <= p.ReorderPoint, cancellationToken)
            .ConfigureAwait(false);
        var toShip = salesCounts.Where(c => c.Status == ErpSalesOrderStatus.Confirmed).ToList();
        return new ErpSummaryDto(
            Sorted(shipped),
            Sorted(received),
            salesCounts.Where(c => c.Status == ErpSalesOrderStatus.Draft).Sum(c => c.Count),
            toShip.Sum(c => c.Count),
            Sorted(toShip.Select(c => new ErpAmountDto(c.Currency, c.Total))),
            openPurchases,
            activeProducts,
            lowStock);
    }

    private static IReadOnlyList<ErpAmountDto> Sorted(IEnumerable<ErpAmountDto> amounts) => [.. amounts.OrderBy(a => a.Currency, StringComparer.Ordinal)];
}
