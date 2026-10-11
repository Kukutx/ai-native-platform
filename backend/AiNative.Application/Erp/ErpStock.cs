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
            .Must(q => Math.Abs(q) < ErpValidation.MaxQuantity).WithMessage("数量太大。")
            .Must(ErpValidation.HasQuantityScale).WithMessage("数量最多 4 位小数。")
            .When(x => x.Request.Quantity.HasValue).OverridePropertyName("quantity");
        RuleFor(x => x.Request.CountedQuantity!.Value).GreaterThanOrEqualTo(0).WithMessage("实际数量不能为负数。")
            .LessThan(ErpValidation.MaxQuantity).WithMessage("数量太大。")
            .Must(ErpValidation.HasQuantityScale).WithMessage("数量最多 4 位小数。")
            .When(x => x.Request.CountedQuantity.HasValue).OverridePropertyName("countedQuantity");
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
    public async Task<PagedResult<ErpStockLevelDto>> HandleAsync(ListErpStockLevelsQuery request, CancellationToken cancellationToken)
    {
        var (page, pageSize) = ErpRecords.Paging(request.Page, request.PageSize);
        var products = db.ErpProducts.AsNoTracking().Where(p => p.TrackInventory);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            products = text.WhereContains(products, request.Search, p => p.Name, p => p.Sku);
        }

        var levels = db.ErpStockLevels.AsNoTracking();
        if (request.WarehouseId is { } warehouseId)
        {
            levels = levels.Where(l => l.WarehouseId == warehouseId);
        }

        if (request.ProductId is { } productId)
        {
            levels = levels.Where(l => l.ProductId == productId);
        }

        var rows = from l in levels
                   join p in products on l.ProductId equals p.Id
                   select new { Level = l, p.Sku, p.ReorderPoint };
        if (request.LowStockOnly)
        {
            // 补货提醒线按产品的全部仓库合计判断，列出这些产品在各仓库的库存。
            rows = rows.Where(r => r.ReorderPoint != null
                && (db.ErpStockLevels.Where(x => x.ProductId == r.Level.ProductId).Sum(x => (decimal?)(x.OnHand - x.Reserved)) ?? 0) <= r.ReorderPoint);
        }

        var total = await rows.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await rows.OrderBy(r => r.Sku).ThenBy(r => r.Level.WarehouseId)
            .Skip((page - 1) * pageSize).Take(pageSize).Select(r => r.Level)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<ErpStockLevelDto>(await ErpMapping.LevelsAsync(db, items, cancellationToken).ConfigureAwait(false), total, page, pageSize);
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
    /// </summary>
    public async Task<ErpStockLevelDto> HandleAsync(AdjustErpStockCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        var product = await TrackedProductAsync(r.ProductId, cancellationToken).ConfigureAwait(false);
        var warehouse = await records.ResolveWarehouseAsync(r.WarehouseId, cancellationToken).ConfigureAwait(false);
        var note = r.Note.Trim();
        decimal delta;
        if (r.CountedQuantity is { } counted)
        {
            var current = await ledger.LockAsync(product.Id, warehouse.Id, cancellationToken).ConfigureAwait(false);
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
            var balance = await records.MoveStockAsync(product.Id, warehouse.Id, ErpStockMovementKind.Adjustment, delta, 0, false, note, null, null, request.RunId, cancellationToken)
                .ConfigureAwait(false);
            if (balance is null)
            {
                var current = await ledger.LockAsync(product.Id, warehouse.Id, cancellationToken).ConfigureAwait(false);
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
    /// 仓库之间调拨：只能调出可用数量（已预留给订单的不动），两边各记一条流水。
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
            product.Id, to.Id, ErpStockMovementKind.TransferIn, r.Quantity, 0, false, note.Length > 0 ? note : $"从「{from.Name}」调入", null, null, request.RunId, cancellationToken)
            .ConfigureAwait(false);
        return await LevelsAsync(product.Id, [from.Id, to.Id], cancellationToken).ConfigureAwait(false);
    }

    private async Task<ErpProduct> TrackedProductAsync(Guid id, CancellationToken cancellationToken)
    {
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
