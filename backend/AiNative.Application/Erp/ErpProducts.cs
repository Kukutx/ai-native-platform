using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Common;
using AiNative.Application.Crm;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Common;
using AiNative.Contracts.Erp;
using AiNative.Domain.Erp;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Erp;

[RequireFeature(ErpFeatures.View)]
public sealed record ListErpProductsQuery(
    string? Search = null,
    string? Category = null,
    bool LowStockOnly = false,
    bool IncludeInactive = false,
    int Page = 1,
    int PageSize = 50) : IQuery<PagedResult<ErpProductDto>>;

[RequireFeature(ErpFeatures.View)]
public sealed record ListErpCategoriesQuery : IQuery<IReadOnlyList<string>>;

[RequireFeature(ErpFeatures.View)]
public sealed record GetErpProductQuery(Guid Id) : IQuery<ErpProductDetailDto>;

[RequireFeature(ErpFeatures.Inventory)]
public sealed record CreateErpProductCommand(SaveErpProductRequest Request) : ICommand<ErpProductDto>;

[RequireFeature(ErpFeatures.Inventory)]
public sealed record UpdateErpProductCommand(Guid Id, SaveErpProductRequest Request) : ICommand<ErpProductDto>;

[RequireFeature(ErpFeatures.Inventory)]
public sealed record DeleteErpProductCommand(Guid Id) : ICommand<Unit>;

[RequireFeature(ErpFeatures.View)]
public sealed record ListErpWarehousesQuery : IQuery<IReadOnlyList<ErpWarehouseDto>>;

[RequireFeature(ErpFeatures.Inventory)]
public sealed record CreateErpWarehouseCommand(SaveErpWarehouseRequest Request) : ICommand<ErpWarehouseDto>;

[RequireFeature(ErpFeatures.Inventory)]
public sealed record UpdateErpWarehouseCommand(Guid Id, SaveErpWarehouseRequest Request) : ICommand<ErpWarehouseDto>;

[RequireFeature(ErpFeatures.Inventory)]
public sealed record DeleteErpWarehouseCommand(Guid Id) : ICommand<Unit>;

public sealed class SaveErpProductRequestValidator : AbstractValidator<SaveErpProductRequest>
{
    public SaveErpProductRequestValidator()
    {
        RuleFor(x => x.Sku).NotEmpty().WithMessage("请填写产品编码（SKU）。").Text(64, "产品编码")
            .Must(s => !s.Trim().Any(char.IsWhiteSpace)).WithMessage("产品编码不能包含空格。").OverridePropertyName("sku");
        RuleFor(x => x.Name).NotEmpty().WithMessage("请填写产品名称。").Text(200, "产品名称").OverridePropertyName("name");
        RuleFor(x => x.Unit).NotEmpty().WithMessage("请填写计量单位。").Text(20, "计量单位").OverridePropertyName("unit");
        RuleFor(x => x.Category).Text(50, "分类").OverridePropertyName("category");
        RuleFor(x => x.Description).Text(10_000, "描述").OverridePropertyName("description");
        RuleFor(x => x.Currency).Currency().OverridePropertyName("currency");
        RuleFor(x => x.Price).GreaterThanOrEqualTo(0).LessThan(ErpValidation.MaxPrice).When(x => x.Price.HasValue)
            .WithMessage("售价必须是不小于 0 的数字。")
            .Must(v => ErpValidation.HasPriceScale(v!.Value)).When(x => x.Price.HasValue).WithMessage("售价最多 4 位小数。")
            .OverridePropertyName("price");
        RuleFor(x => x.Cost).GreaterThanOrEqualTo(0).LessThan(ErpValidation.MaxPrice).When(x => x.Cost.HasValue)
            .WithMessage("采购价必须是不小于 0 的数字。")
            .Must(v => ErpValidation.HasPriceScale(v!.Value)).When(x => x.Cost.HasValue).WithMessage("采购价最多 4 位小数。")
            .OverridePropertyName("cost");
        RuleFor(x => x.ReorderPoint).GreaterThanOrEqualTo(0).LessThan(ErpValidation.MaxQuantity).When(x => x.ReorderPoint.HasValue)
            .WithMessage("补货提醒线必须是不小于 0 的数字。")
            .Must(v => ErpValidation.HasQuantityScale(v!.Value)).When(x => x.ReorderPoint.HasValue).WithMessage("补货提醒线最多 4 位小数。")
            .OverridePropertyName("reorderPoint");
    }
}

public sealed class CreateErpProductCommandValidator : AbstractValidator<CreateErpProductCommand>
{
    public CreateErpProductCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveErpProductRequestValidator());
}

public sealed class UpdateErpProductCommandValidator : AbstractValidator<UpdateErpProductCommand>
{
    public UpdateErpProductCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveErpProductRequestValidator());
}

public sealed class SaveErpWarehouseRequestValidator : AbstractValidator<SaveErpWarehouseRequest>
{
    public SaveErpWarehouseRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("请填写仓库名称。").Text(100, "仓库名称").OverridePropertyName("name");
        RuleFor(x => x.Address).Text(500, "地址").OverridePropertyName("address");
    }
}

public sealed class CreateErpWarehouseCommandValidator : AbstractValidator<CreateErpWarehouseCommand>
{
    public CreateErpWarehouseCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveErpWarehouseRequestValidator());
}

public sealed class UpdateErpWarehouseCommandValidator : AbstractValidator<UpdateErpWarehouseCommand>
{
    public UpdateErpWarehouseCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveErpWarehouseRequestValidator());
}

internal sealed class ErpProductHandlers(
    IAppDbContext db,
    ITenantContext tenant,
    ErpRecords records,
    ITextMatcher text,
    ITransactionLock locks,
    IChangeFeed changes,
    TimeProvider clock) :
    IRequestHandler<ListErpProductsQuery, PagedResult<ErpProductDto>>,
    IRequestHandler<ListErpCategoriesQuery, IReadOnlyList<string>>,
    IRequestHandler<GetErpProductQuery, ErpProductDetailDto>,
    IRequestHandler<CreateErpProductCommand, ErpProductDto>,
    IRequestHandler<UpdateErpProductCommand, ErpProductDto>,
    IRequestHandler<DeleteErpProductCommand, Unit>
{
    public const int MovementPageSize = 50;

    public async Task<PagedResult<ErpProductDto>> HandleAsync(ListErpProductsQuery request, CancellationToken cancellationToken)
    {
        var (page, pageSize) = ErpRecords.Paging(request.Page, request.PageSize);
        var query = db.ErpProducts.AsNoTracking();
        if (!request.IncludeInactive)
        {
            query = query.Where(p => p.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            query = text.WhereContains(query, request.Search, p => p.Name, p => p.Sku);
        }

        if (!string.IsNullOrWhiteSpace(request.Category))
        {
            var category = request.Category.Trim();
            query = query.Where(p => p.Category == category);
        }

        if (request.LowStockOnly)
        {
            query = query.Where(p => p.TrackInventory && p.ReorderPoint != null
                && (db.ErpStockLevels.Where(l => l.ProductId == p.Id).Sum(l => (decimal?)(l.OnHand - l.Reserved)) ?? 0) <= p.ReorderPoint);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query.OrderBy(p => p.Sku)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<ErpProductDto>(await ErpMapping.ProductsAsync(db, items, cancellationToken).ConfigureAwait(false), total, page, pageSize);
    }

    public async Task<IReadOnlyList<string>> HandleAsync(ListErpCategoriesQuery request, CancellationToken cancellationToken)
        => await db.ErpProducts.AsNoTracking().Where(p => p.Category != string.Empty).Select(p => p.Category).Distinct().OrderBy(c => c)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<ErpProductDetailDto> HandleAsync(GetErpProductQuery request, CancellationToken cancellationToken)
    {
        var product = await db.ErpProducts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.Id, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("erp_product", request.Id);
        var levels = await db.ErpStockLevels.AsNoTracking().Where(l => l.ProductId == product.Id)
            .OrderBy(l => l.WarehouseId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var movements = await db.ErpStockMovements.AsNoTracking().Where(m => m.ProductId == product.Id)
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Take(MovementPageSize).ToListAsync(cancellationToken).ConfigureAwait(false);
        var stock = new StockBalance(levels.Sum(l => l.OnHand), levels.Sum(l => l.Reserved));
        return new ErpProductDetailDto(
            product.ToDto(stock),
            await ErpMapping.LevelsAsync(db, levels, cancellationToken).ConfigureAwait(false),
            await ErpMapping.MovementsAsync(db, movements, cancellationToken).ConfigureAwait(false));
    }

    public async Task<ErpProductDto> HandleAsync(CreateErpProductCommand request, CancellationToken cancellationToken)
    {
        var sku = NormalizeSku(request.Request.Sku);
        await EnsureSkuAvailableAsync(sku, null, cancellationToken).ConfigureAwait(false);
        var product = new ErpProduct { Sku = sku, Name = request.Request.Name.Trim(), CreatedBy = tenant.Actor };
        Apply(product, request.Request);
        db.ErpProducts.Add(product);
        changes.Publish(ErpRealtime.Product, product.Id, RealtimeChangeKind.Created);
        return product.ToDto(default);
    }

    public async Task<ErpProductDto> HandleAsync(UpdateErpProductCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;

        // 先给产品行加排他锁再读：确认订单、收货、调整库存会对产品行加共享锁后读取"是否管库存"，
        // 两边互相等待，下面切换前的检查一定能看到对方已提交的结果。
        await db.ErpProducts.Where(p => p.Id == request.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.TrackInventory, p => p.TrackInventory), cancellationToken).ConfigureAwait(false);
        var product = await records.ProductAsync(request.Id, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (r.ExpectedUpdatedAt is { } expected)
        {
            await OptimisticConcurrency.EnsureUnchangedAsync(
                db.ErpProducts, product.Id, expected, now, "erp_product.modified", "产品刚被其他人修改过，已刷新为最新内容，请确认后再保存。", cancellationToken)
                .ConfigureAwait(false);
        }

        var sku = NormalizeSku(r.Sku);
        if (sku != product.Sku)
        {
            await EnsureSkuAvailableAsync(sku, product.Id, cancellationToken).ConfigureAwait(false);
        }

        if (product.TrackInventory != r.TrackInventory)
        {
            // 已确认未发货的订单按确认时是否管库存决定发货、取消时要不要动库存，期间不能切换。
            if (await db.ErpSalesOrders.AnyAsync(o => o.Status == ErpSalesOrderStatus.Confirmed && o.Lines.Any(l => l.ProductId == product.Id), cancellationToken).ConfigureAwait(false))
            {
                throw new ConflictException("erp_product.open_orders", "产品还有已确认未发货的销售订单，等它们发货或取消后再修改是否管库存。");
            }

            if (!r.TrackInventory
                && await db.ErpStockLevels.AnyAsync(l => l.ProductId == product.Id && (l.OnHand != 0 || l.Reserved != 0), cancellationToken).ConfigureAwait(false))
            {
                throw new ConflictException("erp_product.has_stock", "产品还有库存，不能改为不管库存。请先把库存调整为 0。");
            }
        }

        product.Sku = sku;
        product.Name = r.Name.Trim();
        Apply(product, r);
        product.UpdatedAt = now;
        changes.Publish(ErpRealtime.Product, product.Id, RealtimeChangeKind.Updated);
        return (await ErpMapping.ProductsAsync(db, [product], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <summary>
    /// 只能删除从没用过的产品；有订单或库存流水的产品改为停用，保留历史。
    /// </summary>
    public async Task<Unit> HandleAsync(DeleteErpProductCommand request, CancellationToken cancellationToken)
    {
        var product = await records.ProductAsync(request.Id, cancellationToken).ConfigureAwait(false);
        var used = await db.ErpStockMovements.AnyAsync(m => m.ProductId == product.Id, cancellationToken).ConfigureAwait(false)
                   || await db.ErpSalesOrders.AnyAsync(o => o.Lines.Any(l => l.ProductId == product.Id), cancellationToken).ConfigureAwait(false)
                   || await db.ErpPurchaseOrders.AnyAsync(o => o.Lines.Any(l => l.ProductId == product.Id), cancellationToken).ConfigureAwait(false)
                   || await db.ErpStockLevels.AnyAsync(l => l.ProductId == product.Id && (l.OnHand != 0 || l.Reserved != 0), cancellationToken).ConfigureAwait(false);
        if (used)
        {
            throw new ConflictException("erp_product.in_use", $"产品「{product.Name}」已有订单或库存记录，不能删除，可以改为停用。");
        }

        await db.ErpStockLevels.Where(l => l.ProductId == product.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.ErpProducts.Remove(product);
        changes.Publish(ErpRealtime.Product, product.Id, RealtimeChangeKind.Deleted);
        return Unit.Value;
    }

    /// <summary>
    /// 编码统一去空白、转大写，查找和唯一性检查都不区分大小写。
    /// </summary>
    public static string NormalizeSku(string sku) => sku.Trim().ToUpperInvariant();

    private async Task EnsureSkuAvailableAsync(string sku, Guid? exceptId, CancellationToken cancellationToken)
    {
        // 同一编码的并发新建串行执行，避免撞上唯一索引变成 500。
        await locks.AcquireAsync($"erp-sku:{tenant.TenantId}:{sku}", cancellationToken).ConfigureAwait(false);
        if (await db.ErpProducts.AnyAsync(p => p.Sku == sku && p.Id != exceptId, cancellationToken).ConfigureAwait(false))
        {
            throw new ConflictException("erp_product.duplicate_sku", $"编码 {sku} 已被其他产品使用。");
        }
    }

    private static void Apply(ErpProduct product, SaveErpProductRequest r)
    {
        product.Unit = r.Unit.Trim();
        product.Category = r.Category.Trim();
        product.Description = r.Description.Trim();
        product.Price = r.Price;
        product.Cost = r.Cost;
        product.Currency = r.Currency.Trim().ToUpperInvariant();
        product.TrackInventory = r.TrackInventory;
        product.ReorderPoint = r.TrackInventory ? r.ReorderPoint : null;
        product.IsActive = r.IsActive;
    }
}

internal sealed class ErpWarehouseHandlers(IAppDbContext db, ITenantContext tenant, ErpRecords records, ITransactionLock locks, IChangeFeed changes) :
    IRequestHandler<ListErpWarehousesQuery, IReadOnlyList<ErpWarehouseDto>>,
    IRequestHandler<CreateErpWarehouseCommand, ErpWarehouseDto>,
    IRequestHandler<UpdateErpWarehouseCommand, ErpWarehouseDto>,
    IRequestHandler<DeleteErpWarehouseCommand, Unit>
{
    public const int MaxWarehouses = 100;

    public async Task<IReadOnlyList<ErpWarehouseDto>> HandleAsync(ListErpWarehousesQuery request, CancellationToken cancellationToken)
        => [.. (await db.ErpWarehouses.AsNoTracking().OrderByDescending(w => w.IsDefault).ThenBy(w => w.CreatedAt)
                .ToListAsync(cancellationToken).ConfigureAwait(false)).Select(w => w.ToDto())];

    public async Task<ErpWarehouseDto> HandleAsync(CreateErpWarehouseCommand request, CancellationToken cancellationToken)
    {
        await LockAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.ErpWarehouses.ToListAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Count >= MaxWarehouses)
        {
            throw new ConflictException("erp_warehouse.limit", $"最多 {MaxWarehouses} 个仓库。");
        }

        var name = request.Request.Name.Trim();
        EnsureUniqueName(existing, name, null);
        var warehouse = new ErpWarehouse { Name = name, Address = request.Request.Address.Trim(), IsDefault = request.Request.IsDefault || existing.Count == 0 };
        if (warehouse.IsDefault)
        {
            await ClearDefaultAsync(existing, cancellationToken).ConfigureAwait(false);
        }

        db.ErpWarehouses.Add(warehouse);
        changes.Publish(ErpRealtime.Warehouse, warehouse.Id, RealtimeChangeKind.Created);
        return warehouse.ToDto();
    }

    public async Task<ErpWarehouseDto> HandleAsync(UpdateErpWarehouseCommand request, CancellationToken cancellationToken)
    {
        await LockAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.ErpWarehouses.ToListAsync(cancellationToken).ConfigureAwait(false);
        var warehouse = existing.FirstOrDefault(w => w.Id == request.Id) ?? throw new NotFoundException("erp_warehouse", request.Id);
        var name = request.Request.Name.Trim();
        EnsureUniqueName(existing, name, warehouse.Id);
        if (warehouse.IsDefault && !request.Request.IsDefault)
        {
            throw new ConflictException("erp_warehouse.default_required", "需要有一个默认仓库：请把另一个仓库设为默认，这个仓库会自动取消默认。");
        }

        if (request.Request.IsDefault && !warehouse.IsDefault)
        {
            await ClearDefaultAsync(existing, cancellationToken).ConfigureAwait(false);
            warehouse.IsDefault = true;
        }

        warehouse.Name = name;
        warehouse.Address = request.Request.Address.Trim();
        changes.Publish(ErpRealtime.Warehouse, warehouse.Id, RealtimeChangeKind.Updated);
        return warehouse.ToDto();
    }

    /// <summary>
    /// 只能删除空仓库：不是默认仓库，没有库存，也没有订单或流水用过它。
    /// </summary>
    public async Task<Unit> HandleAsync(DeleteErpWarehouseCommand request, CancellationToken cancellationToken)
    {
        await LockAsync(cancellationToken).ConfigureAwait(false);
        var warehouse = await records.WarehouseAsync(request.Id, cancellationToken).ConfigureAwait(false);
        if (warehouse.IsDefault)
        {
            throw new ConflictException("erp_warehouse.default", "不能删除默认仓库，请先把另一个仓库设为默认。");
        }

        if (await db.ErpStockLevels.AnyAsync(l => l.WarehouseId == warehouse.Id && (l.OnHand != 0 || l.Reserved != 0), cancellationToken).ConfigureAwait(false))
        {
            throw new ConflictException("erp_warehouse.has_stock", $"仓库「{warehouse.Name}」还有库存，请先调拨或调整为 0。");
        }

        var used = await db.ErpStockMovements.AnyAsync(m => m.WarehouseId == warehouse.Id, cancellationToken).ConfigureAwait(false)
                   || await db.ErpSalesOrders.AnyAsync(o => o.WarehouseId == warehouse.Id, cancellationToken).ConfigureAwait(false)
                   || await db.ErpPurchaseOrders.AnyAsync(o => o.WarehouseId == warehouse.Id, cancellationToken).ConfigureAwait(false);
        if (used)
        {
            throw new ConflictException("erp_warehouse.in_use", $"仓库「{warehouse.Name}」已有订单或库存流水，不能删除。");
        }

        await db.ErpStockLevels.Where(l => l.WarehouseId == warehouse.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.ErpWarehouses.Remove(warehouse);
        changes.Publish(ErpRealtime.Warehouse, warehouse.Id, RealtimeChangeKind.Deleted);
        return Unit.Value;
    }

    /// <summary>
    /// 仓库的增删改串行执行，保证名称不重复、始终只有一个默认仓库。
    /// </summary>
    private Task LockAsync(CancellationToken cancellationToken) => locks.AcquireAsync($"erp-warehouses:{tenant.TenantId}", cancellationToken);

    private static void EnsureUniqueName(List<ErpWarehouse> existing, string name, Guid? exceptId)
    {
        if (existing.Any(w => w.Id != exceptId && string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ConflictException("erp_warehouse.duplicate_name", $"已经有名为「{name}」的仓库。");
        }
    }

    /// <summary>
    /// 先直接取消原默认仓库，再保存新的默认仓库，"每个空间只有一个默认仓库"的唯一索引不会在中途冲突。
    /// </summary>
    private async Task ClearDefaultAsync(List<ErpWarehouse> existing, CancellationToken cancellationToken)
    {
        await db.ErpWarehouses.Where(w => w.IsDefault).ExecuteUpdateAsync(s => s.SetProperty(w => w.IsDefault, false), cancellationToken).ConfigureAwait(false);
        foreach (var other in existing.Where(w => w.IsDefault))
        {
            db.Entry(other).Property(w => w.IsDefault).CurrentValue = false;
            db.Entry(other).Property(w => w.IsDefault).OriginalValue = false;
            changes.Publish(ErpRealtime.Warehouse, other.Id, RealtimeChangeKind.Updated);
        }
    }
}
