using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Common;
using AiNative.Application.Crm;
using AiNative.Contracts.Common;
using AiNative.Contracts.Erp;
using AiNative.Domain.Erp;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Erp;

[RequireFeature(ErpFeatures.View)]
public sealed record ListErpStockLevelsQuery(
    Guid? WarehouseId = null,
    Guid? ProductId = null,
    string? Search = null,
    bool LowStockOnly = false,
    int Page = 1,
    int PageSize = 50) : IQuery<PagedResult<ErpStockLevelDto>>;

[RequireFeature(ErpFeatures.View)]
public sealed record ListErpStockMovementsQuery(
    Guid? ProductId = null,
    Guid? WarehouseId = null,
    ErpStockMovementKind? Kind = null,
    int Page = 1,
    int PageSize = 50) : IQuery<PagedResult<ErpStockMovementDto>>;

[RequireFeature(ErpFeatures.Inventory)]
public sealed record AdjustErpStockCommand(AdjustErpStockRequest Request, Guid? RunId = null) : ICommand<ErpStockLevelDto>;

[RequireFeature(ErpFeatures.Inventory)]
public sealed record TransferErpStockCommand(TransferErpStockRequest Request, Guid? RunId = null) : ICommand<IReadOnlyList<ErpStockLevelDto>>;

public sealed class AdjustErpStockCommandValidator : AbstractValidator<AdjustErpStockCommand>
{
    public AdjustErpStockCommandValidator()
    {
        RuleFor(x => x.Request).Must(r => r.Quantity.HasValue != r.CountedQuantity.HasValue)
            .WithMessage("请填写变化数量或盘点后的实际数量（二选一）。").OverridePropertyName("quantity");
        RuleFor(x => x.Request.Quantity!.Value).NotEqual(0).WithMessage("变化数量不能为 0。")
            .Must(q => q > -ErpValidation.MaxQuantity && q < ErpValidation.MaxQuantity).WithMessage("数量太大。")
            .Must(ErpValidation.HasQuantityScale).WithMessage("数量最多 4 位小数。")
            .When(x => x.Request.Quantity.HasValue).OverridePropertyName("quantity");
        RuleFor(x => x.Request.CountedQuantity!.Value).GreaterThanOrEqualTo(0).WithMessage("实际数量不能为负数。")
            .LessThan(ErpValidation.MaxQuantity).WithMessage("数量太大。")
            .Must(ErpValidation.HasQuantityScale).WithMessage("数量最多 4 位小数。")
            .When(x => x.Request.CountedQuantity.HasValue).OverridePropertyName("countedQuantity");
        RuleFor(x => x.Request.UnitCost!.Value).GreaterThanOrEqualTo(0).WithMessage("单位成本不能为负数。")
            .LessThan(ErpValidation.MaxPrice).WithMessage("单位成本太大。")
            .Must(c => decimal.Round(c, 6) == c).WithMessage("单位成本最多 6 位小数。")
            .When(x => x.Request.UnitCost.HasValue).OverridePropertyName("unitCost");
        RuleFor(x => x.Request.Note).Text(500, "原因").OverridePropertyName("note");
    }
}

public sealed class TransferErpStockCommandValidator : AbstractValidator<TransferErpStockCommand>
{
    public TransferErpStockCommandValidator()
    {
        RuleFor(x => x.Request.Quantity).GreaterThan(0).WithMessage("调拨数量必须大于 0。")
            .LessThan(ErpValidation.MaxQuantity).WithMessage("数量太大。")
            .Must(ErpValidation.HasQuantityScale).WithMessage("数量最多 4 位小数。").OverridePropertyName("quantity");
        RuleFor(x => x.Request.ToWarehouseId).NotEqual(x => x.Request.FromWarehouseId).WithMessage("调出和调入不能是同一个仓库。").OverridePropertyName("toWarehouseId");
        RuleFor(x => x.Request.Note).Text(500, "备注").OverridePropertyName("note");
    }
}

internal sealed class ErpStockHandlers(IAppDbContext db, ErpRecords records, IStockLedger ledger, ITextMatcher text) :
    IRequestHandler<ListErpStockLevelsQuery, PagedResult<ErpStockLevelDto>>,
    IRequestHandler<ListErpStockMovementsQuery, PagedResult<ErpStockMovementDto>>,
    IRequestHandler<AdjustErpStockCommand, ErpStockLevelDto>,
    IRequestHandler<TransferErpStockCommand, IReadOnlyList<ErpStockLevelDto>>
{
    /// <summary>
    /// 按"管库存的产品 × 仓库"列出库存，没有库存记录的组合按 0 计：选了仓库时列出全部管库存的产品在该仓库的数量；
    /// 不选仓库时列出已有记录的组合，从没动过库存的产品在默认仓库显示一行 0，新产品也不会漏掉。
    /// </summary>
    public async Task<PagedResult<ErpStockLevelDto>> HandleAsync(ListErpStockLevelsQuery request, CancellationToken cancellationToken)
    {
        var (page, pageSize) = ErpRecords.Paging(request.Page, request.PageSize);
        var products = db.ErpProducts.AsNoTracking().Where(p => p.TrackInventory);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            products = text.WhereContains(products, request.Search, p => p.Name, p => p.Sku);
        }

        if (request.ProductId is { } productId)
        {
            products = products.Where(p => p.Id == productId);
        }

        if (request.LowStockOnly)
        {
            // 与产品列表、概况的低库存口径一致：在售产品按全部仓库的可用合计与补货提醒线比较。
            products = products.Where(p => p.IsActive && p.ReorderPoint != null
                && (db.ErpStockLevels.Where(x => x.ProductId == p.Id).Sum(x => (decimal?)(x.OnHand - x.Reserved)) ?? 0) <= p.ReorderPoint);
        }

        var warehouses = db.ErpWarehouses.AsNoTracking();
        if (request.WarehouseId is { } warehouseId)
        {
            warehouses = warehouses.Where(w => w.Id == warehouseId);
        }

        var rows = from p in products
                   from w in warehouses
                   join l in db.ErpStockLevels.AsNoTracking() on new { ProductId = p.Id, WarehouseId = w.Id } equals new { l.ProductId, l.WarehouseId } into matched
                   from l in matched.DefaultIfEmpty()
                   select new { Product = p, Warehouse = w, Level = l };
        if (request.WarehouseId is null)
        {
            rows = rows.Where(r => r.Level != null || (r.Warehouse.IsDefault && !db.ErpStockLevels.Any(x => x.ProductId == r.Product.Id)));
        }

        var total = await rows.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await rows.OrderBy(r => r.Product.Sku).ThenBy(r => r.Warehouse.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(r => new ErpStockLevelDto(
                r.Product.Id,
                r.Product.Sku,
                r.Product.Name,
                r.Product.Unit,
                r.Warehouse.Id,
                r.Warehouse.Name,
                r.Level == null ? 0 : r.Level.OnHand,
                r.Level == null ? 0 : r.Level.Reserved,
                r.Level == null ? 0 : r.Level.OnHand - r.Level.Reserved,
                r.Level == null ? 0 : r.Level.AverageCost,
                r.Level == null ? 0 : Math.Round(r.Level.OnHand * r.Level.AverageCost, 2),
                r.Product.Currency,
                r.Level == null ? r.Product.CreatedAt : r.Level.UpdatedAt))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<ErpStockLevelDto>(items, total, page, pageSize);
    }

    public async Task<PagedResult<ErpStockMovementDto>> HandleAsync(ListErpStockMovementsQuery request, CancellationToken cancellationToken)
    {
        var (page, pageSize) = ErpRecords.Paging(request.Page, request.PageSize);
        var query = db.ErpStockMovements.AsNoTracking();
        if (request.ProductId is { } productId)
        {
            query = query.Where(m => m.ProductId == productId);
        }

        if (request.WarehouseId is { } warehouseId)
        {
            query = query.Where(m => m.WarehouseId == warehouseId);
        }

        if (request.Kind is { } kind)
        {
            query = query.Where(m => m.Kind == kind);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<ErpStockMovementDto>(await ErpMapping.MovementsAsync(db, items, cancellationToken).ConfigureAwait(false), total, page, pageSize);
    }

    /// <summary>
    /// 手工调整：按变化量增减，或按盘点数改成实际数量。出库可以动用已预留的数量（实物短少时如实记录），但在库不能变成负数。
    /// 增加的库存按填写的单位成本计入平均成本；没填时按当前平均成本，仓库里从没有过成本时用产品的参考采购价。
    /// </summary>
    public async Task<ErpStockLevelDto> HandleAsync(AdjustErpStockCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        var product = await TrackedProductAsync(r.ProductId, cancellationToken).ConfigureAwait(false);
        var warehouse = await records.ResolveWarehouseAsync(r.WarehouseId, cancellationToken).ConfigureAwait(false);
        var note = r.Note.Trim();
        var current = await ledger.LockAsync(product.Id, warehouse.Id, cancellationToken).ConfigureAwait(false);
        decimal delta;
        if (r.CountedQuantity is { } counted)
        {
            delta = counted - current.OnHand;
            if (note.Length == 0)
            {
                note = $"盘点：{ErpRecords.Quantity(current.OnHand)} → {ErpRecords.Quantity(counted)}";
            }
        }
        else
        {
            delta = r.Quantity!.Value;
        }

        if (delta != 0)
        {
            var unitCost = r.UnitCost ?? (current is { OnHand: 0, AverageCost: 0 } ? product.Cost : null);
            var balance = await records.MoveStockAsync(
                    product.Id, warehouse.Id, ErpStockMovementKind.Adjustment, delta, 0, false, note, null, null, request.RunId, cancellationToken, unitCost)
                .ConfigureAwait(false);
            if (balance is null)
            {
                throw new ConflictException(
                    "erp.insufficient_stock",
                    $"「{warehouse.Name}」里 {product.Name} 只有 {ErpRecords.Quantity(current.OnHand)} {product.Unit}，不能减少 {ErpRecords.Quantity(-delta)} {product.Unit}。");
            }

            if (delta < 0)
            {
                await records.CheckLowStockAsync(new Dictionary<Guid, decimal> { [product.Id] = -delta }, cancellationToken).ConfigureAwait(false);
            }
        }

        return (await LevelsAsync(product.Id, [warehouse.Id], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <summary>
    /// 仓库之间调拨：只能调出可用数量（已预留给订单的不动），两边各记一条流水。调入方按调出仓库的平均成本入库，库存金额随货走。
    /// </summary>
    public async Task<IReadOnlyList<ErpStockLevelDto>> HandleAsync(TransferErpStockCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        var product = await TrackedProductAsync(r.ProductId, cancellationToken).ConfigureAwait(false);
        var from = await records.WarehouseAsync(r.FromWarehouseId, cancellationToken).ConfigureAwait(false);
        var to = await records.WarehouseAsync(r.ToWarehouseId, cancellationToken).ConfigureAwait(false);

        // 按仓库 ID 的固定顺序锁两行库存，相向的两次调拨不会互相等待成死锁。
        foreach (var warehouseId in new[] { from.Id, to.Id }.Order())
        {
            await ledger.LockAsync(product.Id, warehouseId, cancellationToken).ConfigureAwait(false);
        }

        var note = r.Note.Trim();
        var moved = await records.MoveStockAsync(
            product.Id, from.Id, ErpStockMovementKind.TransferOut, -r.Quantity, 0, true, note.Length > 0 ? note : $"调拨到「{to.Name}」", null, null, request.RunId, cancellationToken)
            .ConfigureAwait(false);
        if (moved is null)
        {
            var current = await ledger.LockAsync(product.Id, from.Id, cancellationToken).ConfigureAwait(false);
            throw new ConflictException(
                "erp.insufficient_stock",
                $"「{from.Name}」里 {product.Name} 可用 {ErpRecords.Quantity(current.Available)} {product.Unit}，不够调出 {ErpRecords.Quantity(r.Quantity)} {product.Unit}。");
        }

        await records.MoveStockAsync(
                product.Id, to.Id, ErpStockMovementKind.TransferIn, r.Quantity, 0, false, note.Length > 0 ? note : $"从「{from.Name}」调入", null, null, request.RunId, cancellationToken,
                moved.UnitCost)
            .ConfigureAwait(false);
        return await LevelsAsync(product.Id, [from.Id, to.Id], cancellationToken).ConfigureAwait(false);
    }

    private async Task<ErpProduct> TrackedProductAsync(Guid id, CancellationToken cancellationToken)
    {
        // 共享锁住产品后再读，调整期间不会被改成不管库存。
        await ledger.LockProductsAsync([id], cancellationToken).ConfigureAwait(false);
        var product = await records.ProductAsync(id, cancellationToken).ConfigureAwait(false);
        return product.TrackInventory
            ? product
            : throw new ConflictException("erp_product.untracked", $"产品「{product.Name}」不管库存，不需要调整。");
    }

    private async Task<IReadOnlyList<ErpStockLevelDto>> LevelsAsync(Guid productId, Guid[] warehouseIds, CancellationToken cancellationToken)
    {
        var levels = await db.ErpStockLevels.AsNoTracking().Where(l => l.ProductId == productId && warehouseIds.Contains(l.WarehouseId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var ordered = warehouseIds.Select(id => levels.First(l => l.WarehouseId == id)).ToList();
        return await ErpMapping.LevelsAsync(db, ordered, cancellationToken).ConfigureAwait(false);
    }
}
