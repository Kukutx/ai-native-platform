using AiNative.Application.Abstractions;
using AiNative.Application.Erp;
using AiNative.Application.Tenancy;
using AiNative.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Infrastructure.Erp;

/// <summary>
/// 库存行的原子读写：每次变化都是一条带条件的 UPDATE，条件与修改在同一条语句里完成，
/// 并发的两次扣减不会都成功，也就不会超卖或扣成负数。行锁持有到事务结束。
/// </summary>
internal sealed class StockLedger(AppDbContext db, ITenantContext tenant, TimeProvider clock) : IStockLedger
{
    public async Task<StockBalance> LockAsync(Guid productId, Guid warehouseId, CancellationToken cancellationToken)
    {
        await EnsureRowAsync(productId, warehouseId, cancellationToken).ConfigureAwait(false);
        var rows = await db.Database.SqlQuery<StockRow>($"""
            SELECT on_hand, reserved
            FROM erp_stock_levels
            WHERE tenant_id = {tenant.TenantId} AND product_id = {productId} AND warehouse_id = {warehouseId}
            FOR UPDATE
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new StockBalance(rows[0].OnHand, rows[0].Reserved);
    }

    public async Task<StockBalance?> TryChangeAsync(
        Guid productId,
        Guid warehouseId,
        decimal onHandDelta,
        decimal reservedDelta,
        bool requireAvailable,
        CancellationToken cancellationToken)
    {
        await EnsureRowAsync(productId, warehouseId, cancellationToken).ConfigureAwait(false);
        var rows = await db.Database.SqlQuery<StockRow>($"""
            UPDATE erp_stock_levels
            SET on_hand = on_hand + {onHandDelta}, reserved = reserved + {reservedDelta}, updated_at = {clock.GetUtcNow()}
            WHERE tenant_id = {tenant.TenantId} AND product_id = {productId} AND warehouse_id = {warehouseId}
              AND on_hand + {onHandDelta} >= 0
              AND reserved + {reservedDelta} >= 0
              AND (NOT {requireAvailable} OR on_hand + {onHandDelta} - reserved - {reservedDelta} >= 0)
            RETURNING on_hand, reserved
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count == 1 ? new StockBalance(rows[0].OnHand, rows[0].Reserved) : null;
    }

    /// <summary>
    /// 第一次碰到某个产品与仓库的组合时补一行 0；并发插入由唯一索引兜底，后到的什么都不做。
    /// </summary>
    private Task EnsureRowAsync(Guid productId, Guid warehouseId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        return db.Database.ExecuteSqlAsync($"""
            INSERT INTO erp_stock_levels (id, tenant_id, product_id, warehouse_id, on_hand, reserved, created_at, updated_at)
            VALUES ({Guid.CreateVersion7()}, {tenant.TenantId}, {productId}, {warehouseId}, 0, 0, {now}, {now})
            ON CONFLICT (product_id, warehouse_id) DO NOTHING
            """, cancellationToken);
    }

    /// <summary>
    /// 原始 SQL 的结果类型同样遵循 snake_case 命名约定：列名 on_hand、reserved。
    /// </summary>
    private sealed record StockRow(decimal OnHand, decimal Reserved);
}

/// <summary>
/// 租户内编号序列：不存在就从 1 开始，存在就原子加一，随当前事务提交或回滚。
/// </summary>
internal sealed class SequenceAllocator(AppDbContext db, ITenantContext tenant) : ISequenceAllocator
{
    public async Task<long> NextAsync(string name, CancellationToken cancellationToken = default)
    {
        var values = await db.Database.SqlQuery<long>($"""
            INSERT INTO tenant_sequences (tenant_id, name, value) VALUES ({tenant.TenantId}, {name}, 1)
            ON CONFLICT (tenant_id, name) DO UPDATE SET value = tenant_sequences.value + 1
            RETURNING value AS "Value"
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        return values[0];
    }
}
