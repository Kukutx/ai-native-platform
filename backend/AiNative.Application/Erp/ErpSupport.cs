using System.Text.RegularExpressions;
using AiNative.Application.Abstractions;
using AiNative.Application.Common;
using AiNative.Application.Modules;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Common;
using AiNative.Contracts.Erp;
using AiNative.Domain.Common;
using AiNative.Domain.Erp;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Erp;

/// <summary>
/// 某个产品在某个仓库的在库与预留数量。
/// </summary>
public readonly record struct StockBalance(decimal OnHand, decimal Reserved)
{
    public decimal Available => OnHand - Reserved;
}

/// <summary>
/// 库存行的原子读写。实现用条件更新保证并发下不会超卖、不会扣成负数，随当前事务提交或回滚。
/// </summary>
public interface IStockLedger
{
    /// <summary>
    /// 锁住某个产品在某个仓库的库存行（没有则先建一行 0），返回当前数量。先看数量再决定怎么改时使用（例如盘点）。
    /// </summary>
    Task<StockBalance> LockAsync(Guid productId, Guid warehouseId, CancellationToken cancellationToken);

    /// <summary>
    /// 原子地改变在库与预留数量。改完后在库或预留小于 0、或 <paramref name="requireAvailable"/> 时可用数量小于 0，
    /// 则什么都不改并返回 null。
    /// </summary>
    Task<StockBalance?> TryChangeAsync(
        Guid productId,
        Guid warehouseId,
        decimal onHandDelta,
        decimal reservedDelta,
        bool requireAvailable,
        CancellationToken cancellationToken);

    /// <summary>
    /// 以共享方式锁住这些产品行直到事务结束，之后读到的"是否管库存"在本事务内不会再变。
    /// 凡是按"是否管库存"决定动不动库存的操作都先调用它；修改产品时对产品行加排他锁，两者互相等待而不是交错。
    /// </summary>
    Task LockProductsAsync(IEnumerable<Guid> productIds, CancellationToken cancellationToken);
}

/// <summary>
/// 实体到 DTO 的批量映射：关联的名称、库存合计和操作者一次查齐，列表不逐条查询。
/// </summary>
internal static class ErpMapping
{
    public static async Task<IReadOnlyList<ErpProductDto>> ProductsAsync(IAppDbContext db, IReadOnlyList<ErpProduct> products, CancellationToken cancellationToken)
    {
        if (products.Count == 0)
        {
            return [];
        }

        var ids = products.Select(p => p.Id).ToList();
        var totals = await db.ErpStockLevels.AsNoTracking()
            .Where(l => ids.Contains(l.ProductId))
            .GroupBy(l => l.ProductId)
            .Select(g => new { g.Key, OnHand = g.Sum(l => l.OnHand), Reserved = g.Sum(l => l.Reserved) })
            .ToDictionaryAsync(x => x.Key, x => new StockBalance(x.OnHand, x.Reserved), cancellationToken)
            .ConfigureAwait(false);
        return [.. products.Select(p => p.ToDto(totals.GetValueOrDefault(p.Id)))];
    }

    public static ErpProductDto ToDto(this ErpProduct p, StockBalance stock)
    {
        var balance = p.TrackInventory ? stock : default;
        return new ErpProductDto(
            p.Id,
            p.Sku,
            p.Name,
            p.Unit,
            p.Category,
            p.Description,
            p.Price,
            p.Cost,
            p.Currency,
            p.TrackInventory,
            p.ReorderPoint,
            p.IsActive,
            balance.OnHand,
            balance.Reserved,
            balance.Available,
            ErpStockRules.IsLow(p, balance.Available),
            p.CreatedAt,
            p.UpdatedAt);
    }

    public static ErpWarehouseDto ToDto(this ErpWarehouse w) => new(w.Id, w.Name, w.Address, w.IsDefault, w.CreatedAt);

    public static async Task<IReadOnlyList<ErpStockLevelDto>> LevelsAsync(IAppDbContext db, IReadOnlyList<ErpStockLevel> levels, CancellationToken cancellationToken)
    {
        if (levels.Count == 0)
        {
            return [];
        }

        var products = await ProductInfoAsync(db, levels.Select(l => l.ProductId), cancellationToken).ConfigureAwait(false);
        var warehouses = await WarehouseNamesAsync(db, levels.Select(l => l.WarehouseId), cancellationToken).ConfigureAwait(false);
        return
        [
            .. levels.Select(l =>
            {
                var product = products.GetValueOrDefault(l.ProductId);
                return new ErpStockLevelDto(
                    l.ProductId,
                    product?.Sku ?? string.Empty,
                    product?.Name ?? string.Empty,
                    product?.Unit ?? string.Empty,
                    l.WarehouseId,
                    warehouses.GetValueOrDefault(l.WarehouseId) ?? string.Empty,
                    l.OnHand,
                    l.Reserved,
                    l.OnHand - l.Reserved,
                    l.UpdatedAt);
            }),
        ];
    }

    public static async Task<IReadOnlyList<ErpStockMovementDto>> MovementsAsync(IAppDbContext db, IReadOnlyList<ErpStockMovement> movements, CancellationToken cancellationToken)
    {
        if (movements.Count == 0)
        {
            return [];
        }

        var products = await ProductInfoAsync(db, movements.Select(m => m.ProductId), cancellationToken).ConfigureAwait(false);
        var warehouses = await WarehouseNamesAsync(db, movements.Select(m => m.WarehouseId), cancellationToken).ConfigureAwait(false);
        var salesIds = movements.Where(m => m.SalesOrderId.HasValue).Select(m => m.SalesOrderId!.Value).Distinct().ToList();
        var sales = salesIds.Count == 0
            ? []
            : await db.ErpSalesOrders.AsNoTracking().Where(o => salesIds.Contains(o.Id))
                .ToDictionaryAsync(o => o.Id, o => o.Number, cancellationToken).ConfigureAwait(false);
        var purchaseIds = movements.Where(m => m.PurchaseOrderId.HasValue).Select(m => m.PurchaseOrderId!.Value).Distinct().ToList();
        var purchases = purchaseIds.Count == 0
            ? []
            : await db.ErpPurchaseOrders.AsNoTracking().Where(o => purchaseIds.Contains(o.Id))
                .ToDictionaryAsync(o => o.Id, o => o.Number, cancellationToken).ConfigureAwait(false);
        var actors = await ActorNames.ResolveAsync(db, movements.Select(m => m.Actor), cancellationToken).ConfigureAwait(false);
        return
        [
            .. movements.Select(m =>
            {
                var product = products.GetValueOrDefault(m.ProductId);
                return new ErpStockMovementDto(
                    m.Id,
                    m.ProductId,
                    product?.Sku ?? string.Empty,
                    product?.Name ?? string.Empty,
                    product?.Unit ?? string.Empty,
                    m.WarehouseId,
                    warehouses.GetValueOrDefault(m.WarehouseId) ?? string.Empty,
                    m.Kind,
                    m.Quantity,
                    m.BalanceAfter,
                    m.Note,
                    m.SalesOrderId,
                    NameOf(sales, m.SalesOrderId),
                    m.PurchaseOrderId,
                    NameOf(purchases, m.PurchaseOrderId),
                    actors(m.Actor),
                    m.CreatedAt);
            }),
        ];
    }

    public static async Task<IReadOnlyList<ErpSalesOrderDto>> SalesOrdersAsync(IAppDbContext db, IReadOnlyList<ErpSalesOrder> orders, CancellationToken cancellationToken)
    {
        if (orders.Count == 0)
        {
            return [];
        }

        var products = await ProductInfoAsync(db, orders.SelectMany(o => o.Lines).Select(l => l.ProductId), cancellationToken).ConfigureAwait(false);
        var warehouses = await WarehouseNamesAsync(db, orders.Select(o => o.WarehouseId), cancellationToken).ConfigureAwait(false);
        var companies = await CompanyNamesAsync(db, orders.Select(o => o.CompanyId), cancellationToken).ConfigureAwait(false);
        var contactIds = orders.Where(o => o.ContactId.HasValue).Select(o => o.ContactId!.Value).Distinct().ToList();
        var contacts = contactIds.Count == 0
            ? []
            : await db.CrmContacts.AsNoTracking().Where(c => contactIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken).ConfigureAwait(false);
        var dealIds = orders.Where(o => o.DealId.HasValue).Select(o => o.DealId!.Value).Distinct().ToList();
        var deals = dealIds.Count == 0
            ? []
            : await db.CrmDeals.AsNoTracking().Where(d => dealIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.Title, cancellationToken).ConfigureAwait(false);
        var actors = await ActorNames.ResolveAsync(
            db,
            orders.Select(o => o.CreatedBy).Concat(orders.Where(o => o.OwnerUserId.HasValue).Select(o => Actor.ForUser(o.OwnerUserId!.Value))),
            cancellationToken).ConfigureAwait(false);
        return
        [
            .. orders.Select(o => new ErpSalesOrderDto(
                o.Id,
                o.Number,
                o.Status,
                o.CompanyId,
                NameOf(companies, o.CompanyId),
                o.ContactId,
                NameOf(contacts, o.ContactId),
                o.DealId,
                NameOf(deals, o.DealId),
                o.WarehouseId,
                warehouses.GetValueOrDefault(o.WarehouseId) ?? string.Empty,
                o.Currency,
                o.OrderDate,
                o.Note,
                o.Total,
                Lines(o.Lines, products),
                o.OwnerUserId is { } owner ? actors(Actor.ForUser(owner)) : null,
                actors(o.CreatedBy),
                o.CreatedAt,
                o.UpdatedAt,
                o.ConfirmedAt,
                o.ShippedAt,
                o.CancelledAt)),
        ];
    }

    public static async Task<IReadOnlyList<ErpPurchaseOrderDto>> PurchaseOrdersAsync(IAppDbContext db, IReadOnlyList<ErpPurchaseOrder> orders, CancellationToken cancellationToken)
    {
        if (orders.Count == 0)
        {
            return [];
        }

        var products = await ProductInfoAsync(db, orders.SelectMany(o => o.Lines).Select(l => l.ProductId), cancellationToken).ConfigureAwait(false);
        var warehouses = await WarehouseNamesAsync(db, orders.Select(o => o.WarehouseId), cancellationToken).ConfigureAwait(false);
        var suppliers = await CompanyNamesAsync(db, orders.Select(o => o.SupplierId), cancellationToken).ConfigureAwait(false);
        var actors = await ActorNames.ResolveAsync(db, orders.Select(o => o.CreatedBy), cancellationToken).ConfigureAwait(false);
        return
        [
            .. orders.Select(o => new ErpPurchaseOrderDto(
                o.Id,
                o.Number,
                o.Status,
                o.SupplierId,
                NameOf(suppliers, o.SupplierId),
                o.WarehouseId,
                warehouses.GetValueOrDefault(o.WarehouseId) ?? string.Empty,
                o.Currency,
                o.OrderDate,
                o.ExpectedDate,
                o.Note,
                o.Total,
                Lines(o.Lines, products),
                actors(o.CreatedBy),
                o.CreatedAt,
                o.UpdatedAt,
                o.OrderedAt,
                o.ReceivedAt,
                o.CancelledAt)),
        ];
    }

    private static List<ErpOrderLineDto> Lines(IEnumerable<ErpOrderLine> lines, Dictionary<Guid, ProductInfo> products)
        =>
        [
            .. lines.Select(l =>
            {
                var product = products.GetValueOrDefault(l.ProductId);
                return new ErpOrderLineDto(l.ProductId, product?.Sku ?? string.Empty, product?.Name ?? string.Empty, product?.Unit ?? string.Empty, l.Quantity, l.UnitPrice, l.Amount);
            }),
        ];

    private static string? NameOf(Dictionary<Guid, string> names, Guid? id) => id is { } key ? names.GetValueOrDefault(key) : null;

    private sealed record ProductInfo(string Sku, string Name, string Unit);

    private static async Task<Dictionary<Guid, ProductInfo>> ProductInfoAsync(IAppDbContext db, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0
            ? []
            : await db.ErpProducts.AsNoTracking().Where(p => list.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => new ProductInfo(p.Sku, p.Name, p.Unit), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Dictionary<Guid, string>> WarehouseNamesAsync(IAppDbContext db, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0
            ? []
            : await db.ErpWarehouses.AsNoTracking().Where(w => list.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Name, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Dictionary<Guid, string>> CompanyNamesAsync(IAppDbContext db, IEnumerable<Guid?> ids, CancellationToken cancellationToken)
    {
        var list = ids.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
        return list.Count == 0
            ? []
            : await db.CrmCompanies.AsNoTracking().Where(c => list.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken).ConfigureAwait(false);
    }
}

internal static class ErpStockRules
{
    /// <summary>
    /// 低库存：管库存、设了补货提醒线，且可用数量不高于提醒线。
    /// </summary>
    public static bool IsLow(ErpProduct product, decimal available)
        => product.TrackInventory && product.ReorderPoint is { } point && available <= point;
}

/// <summary>
/// 进销存各处理器共用的查找、校验与写入辅助。
/// </summary>
internal sealed class ErpRecords(
    IAppDbContext db,
    ITenantContext tenant,
    IPlatformEvents events,
    ISequenceAllocator sequences,
    IStockLedger ledger,
    IChangeFeed changes)
{
    public async Task<ErpProduct> ProductAsync(Guid id, CancellationToken cancellationToken)
        => await db.ErpProducts.FirstOrDefaultAsync(p => p.Id == id, cancellationToken).ConfigureAwait(false)
           ?? throw new NotFoundException("erp_product", id);

    /// <summary>
    /// 先以共享锁锁住这些产品，再返回其中管库存的产品：到事务结束前它们是否管库存不会被修改产品的操作改掉。
    /// </summary>
    public async Task<Dictionary<Guid, ErpProduct>> TrackedProductsAsync(IEnumerable<Guid> productIds, CancellationToken cancellationToken)
    {
        var ids = productIds.Distinct().ToList();
        await ledger.LockProductsAsync(ids, cancellationToken).ConfigureAwait(false);
        return await db.ErpProducts.AsNoTracking().Where(p => ids.Contains(p.Id) && p.TrackInventory)
            .ToDictionaryAsync(p => p.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ErpWarehouse> WarehouseAsync(Guid id, CancellationToken cancellationToken)
        => await db.ErpWarehouses.FirstOrDefaultAsync(w => w.Id == id, cancellationToken).ConfigureAwait(false)
           ?? throw new NotFoundException("erp_warehouse", id);

    /// <summary>
    /// 指定了仓库就用它，否则用默认仓库。
    /// </summary>
    public async Task<ErpWarehouse> ResolveWarehouseAsync(Guid? id, CancellationToken cancellationToken)
    {
        if (id is { } warehouseId)
        {
            return await WarehouseAsync(warehouseId, cancellationToken).ConfigureAwait(false);
        }

        return await db.ErpWarehouses.AsNoTracking().OrderByDescending(w => w.IsDefault).ThenBy(w => w.CreatedAt)
                   .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
               ?? throw new ConflictException("erp.no_warehouse", "还没有仓库，请先在库存页面添加仓库。");
    }

    public async Task<ErpSalesOrder> SalesOrderAsync(Guid id, CancellationToken cancellationToken)
        => await db.ErpSalesOrders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken).ConfigureAwait(false)
           ?? throw new NotFoundException("erp_sales_order", id);

    public async Task<ErpPurchaseOrder> PurchaseOrderAsync(Guid id, CancellationToken cancellationToken)
        => await db.ErpPurchaseOrders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken).ConfigureAwait(false)
           ?? throw new NotFoundException("erp_purchase_order", id);

    public async Task EnsureOwnerAsync(Guid? userId, CancellationToken cancellationToken)
    {
        if (userId is { } id && !await db.Memberships.AnyAsync(m => m.UserId == id, cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException("member", id);
        }
    }

    /// <summary>
    /// 新订单号：前缀加空间内递增的序号，例如 SO-0001。
    /// </summary>
    public async Task<string> NextNumberAsync(string prefix, CancellationToken cancellationToken)
        => $"{prefix}-{await sequences.NextAsync($"erp.{prefix.ToLowerInvariant()}", cancellationToken).ConfigureAwait(false):D4}";

    /// <summary>
    /// 把订单行请求变成订单行：产品必须存在且在售，同一产品合并成一行；没填单价时取产品价格（货币须与订单一致）。
    /// </summary>
    public async Task<(List<ErpOrderLine> Lines, Dictionary<Guid, ErpProduct> Products)> BuildLinesAsync(
        IReadOnlyList<ErpOrderLineRequest> requests,
        string currency,
        Func<ErpProduct, decimal?> defaultPrice,
        CancellationToken cancellationToken)
    {
        var ids = requests.Select(r => r.ProductId).Distinct().ToList();
        var products = await db.ErpProducts.AsNoTracking().Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken).ConfigureAwait(false);
        var errors = new List<string>();
        var lines = new List<ErpOrderLine>();
        foreach (var group in requests.GroupBy(r => r.ProductId))
        {
            if (!products.TryGetValue(group.Key, out var product))
            {
                throw new NotFoundException("erp_product", group.Key);
            }

            if (!product.IsActive)
            {
                errors.Add($"产品「{product.Name}」已停用。");
                continue;
            }

            var quantity = group.Sum(r => r.Quantity);
            var price = group.Select(r => r.UnitPrice).FirstOrDefault(p => p.HasValue);
            if (price is null)
            {
                price = product.Currency == currency ? defaultPrice(product) : null;
                if (price is null)
                {
                    errors.Add(product.Currency == currency
                        ? $"产品「{product.Name}」没有设置价格，请填写单价。"
                        : $"产品「{product.Name}」的价格是 {product.Currency}，订单是 {currency}，请填写单价。");
                    continue;
                }
            }

            // 同一产品的多行合并后数量也不能超过上限；金额按单价算出后同样要放得进数据库。
            if (quantity >= ErpValidation.MaxQuantity)
            {
                errors.Add($"产品「{product.Name}」的数量太大。");
                continue;
            }

            var line = ErpOrderLine.Create(product.Id, quantity, price.Value);
            if (line.Amount >= ErpValidation.MaxAmount)
            {
                errors.Add($"产品「{product.Name}」的金额太大。");
                continue;
            }

            lines.Add(line);
        }

        if (errors.Count == 0 && lines.Sum(l => l.Amount) >= ErpValidation.MaxAmount)
        {
            errors.Add("订单合计金额太大，请拆成多张订单。");
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(new Dictionary<string, string[]> { ["lines"] = [.. errors] });
        }

        return (lines, products);
    }

    /// <summary>
    /// 改变在库数量并记一条流水。数量不够时返回 null（什么都没改）。
    /// </summary>
    /// <param name="requireAvailable">只能动用可用数量（不碰已预留给订单的部分）。</param>
    public async Task<StockBalance?> MoveStockAsync(
        Guid productId,
        Guid warehouseId,
        ErpStockMovementKind kind,
        decimal quantity,
        decimal reservedDelta,
        bool requireAvailable,
        string note,
        Guid? salesOrderId,
        Guid? purchaseOrderId,
        Guid? runId,
        CancellationToken cancellationToken)
    {
        var balance = await ledger.TryChangeAsync(productId, warehouseId, quantity, reservedDelta, requireAvailable, cancellationToken).ConfigureAwait(false);
        if (balance is { } after)
        {
            db.ErpStockMovements.Add(new ErpStockMovement
            {
                ProductId = productId,
                WarehouseId = warehouseId,
                Kind = kind,
                Quantity = quantity,
                BalanceAfter = after.OnHand,
                Note = note,
                SalesOrderId = salesOrderId,
                PurchaseOrderId = purchaseOrderId,
                Actor = tenant.Actor,
                RunId = runId,
            });
            changes.Publish(ErpRealtime.Stock, productId, RealtimeChangeKind.Updated);
        }

        return balance;
    }

    /// <summary>
    /// 可用库存减少后检查补货提醒线：从线上跌到线上或以下的产品各发一次低库存事件。
    /// </summary>
    /// <param name="decreases">每个产品可用数量的减少量（全部仓库合计）。</param>
    public async Task CheckLowStockAsync(IReadOnlyDictionary<Guid, decimal> decreases, CancellationToken cancellationToken)
    {
        var ids = decreases.Where(d => d.Value > 0).Select(d => d.Key).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var products = await db.ErpProducts.AsNoTracking()
            .Where(p => ids.Contains(p.Id) && p.TrackInventory && p.ReorderPoint != null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (products.Count == 0)
        {
            return;
        }

        var productIds = products.Select(p => p.Id).ToList();
        var totals = await db.ErpStockLevels.AsNoTracking().Where(l => productIds.Contains(l.ProductId))
            .GroupBy(l => l.ProductId)
            .Select(g => new { g.Key, OnHand = g.Sum(l => l.OnHand), Reserved = g.Sum(l => l.Reserved) })
            .ToDictionaryAsync(x => x.Key, x => new StockBalance(x.OnHand, x.Reserved), cancellationToken).ConfigureAwait(false);
        foreach (var product in products)
        {
            var stock = totals.GetValueOrDefault(product.Id);
            var before = stock.Available + decreases[product.Id];
            if (ErpStockRules.IsLow(product, stock.Available) && !ErpStockRules.IsLow(product, before))
            {
                var dto = product.ToDto(stock);
                await PublishAsync(ErpEventKeys.StockLow, product.Id, () => Task.FromResult(new Dictionary<string, object?> { ["product"] = dto }), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 发布进销存事件（订阅的工作流随本事务创建运行）。载荷只在有订阅者时构造，自动带上操作者。
    /// </summary>
    public Task PublishAsync(string eventType, Guid subjectId, Func<Task<Dictionary<string, object?>>> payload, CancellationToken cancellationToken)
        => events.PublishAsync(eventType, subjectId, async () =>
        {
            var data = await payload().ConfigureAwait(false);
            var actors = await ActorNames.ResolveAsync(db, [tenant.Actor], cancellationToken).ConfigureAwait(false);
            data["actor"] = actors(tenant.Actor);
            return data;
        }, cancellationToken);

    public static (int Page, int PageSize) Paging(int page, int pageSize) => (Math.Max(1, page), Math.Clamp(pageSize, 1, 200));

    /// <summary>
    /// 库存不足时的说明，例如「A100 螺丝 缺 3 件（可用 2）」。
    /// </summary>
    public static string Shortage(ErpProduct product, decimal needed, decimal available)
        => $"{product.Sku} {product.Name} 缺 {Quantity(needed - Math.Max(available, 0))} {product.Unit}（可用 {Quantity(available)}）";

    public static string Quantity(decimal value) => value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
}

internal static partial class ErpValidation
{
    public const decimal MaxQuantity = 1_000_000_000m;
    public const decimal MaxPrice = 1_000_000_000_000m;

    /// <summary>
    /// 行金额与订单合计的上限。数据库按 numeric(18,2) 保存（整数部分最多 16 位），留出余量。
    /// </summary>
    public const decimal MaxAmount = 1_000_000_000_000_000m;

    public const int MaxLines = 200;

    public static IRuleBuilderOptions<T, string> Currency<T>(this IRuleBuilder<T, string> rule)
        => rule.Must(c => CurrencyPattern().IsMatch((c ?? string.Empty).Trim().ToUpperInvariant())).WithMessage("货币请填写 3 位字母代码，例如 CNY、USD。");

    /// <summary>
    /// 数量最多 4 位小数，与数据库精度一致。
    /// </summary>
    public static bool HasQuantityScale(decimal value) => decimal.Round(value, 4) == value;

    /// <summary>
    /// 单价最多 4 位小数，与数据库精度一致；否则保存后单价被截断，金额就无法由数量和单价算出。
    /// </summary>
    public static bool HasPriceScale(decimal value) => decimal.Round(value, 4) == value;

    public static void Lines<T>(this AbstractValidator<T> validator, System.Linq.Expressions.Expression<Func<T, IEnumerable<ErpOrderLineRequest>>> lines)
    {
        validator.RuleFor(lines).NotEmpty().WithMessage("订单至少需要一行产品。")
            .Must(l => l.Count() <= MaxLines).WithMessage($"订单最多 {MaxLines} 行。").OverridePropertyName("lines");
        validator.RuleForEach(lines).ChildRules(line =>
        {
            line.RuleFor(l => l.Quantity).GreaterThan(0).WithMessage("数量必须大于 0。")
                .LessThan(MaxQuantity).WithMessage("数量太大。")
                .Must(HasQuantityScale).WithMessage("数量最多 4 位小数。");
            line.RuleFor(l => l.UnitPrice).GreaterThanOrEqualTo(0).LessThan(MaxPrice).When(l => l.UnitPrice.HasValue).WithMessage("单价必须是不小于 0 的数字。")
                .Must(p => HasPriceScale(p!.Value)).When(l => l.UnitPrice.HasValue).WithMessage("单价最多 4 位小数。");
        }).OverridePropertyName("lines");
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();
}
