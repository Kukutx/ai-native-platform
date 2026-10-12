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

/// <param name="Pending">只看还没收完的订单（已下单和部分到货）。</param>
[RequireFeature(ErpFeatures.View)]
public sealed record ListErpPurchaseOrdersQuery(
    string? Search = null,
    ErpPurchaseOrderStatus? Status = null,
    Guid? SupplierId = null,
    int Page = 1,
    int PageSize = 50,
    bool Pending = false) : IQuery<PagedResult<ErpPurchaseOrderDto>>;

[RequireFeature(ErpFeatures.View)]
public sealed record GetErpPurchaseOrderQuery(Guid Id) : IQuery<ErpPurchaseOrderDto>;

[RequireFeature(ErpFeatures.Purchase)]
public sealed record CreateErpPurchaseOrderCommand(SaveErpPurchaseOrderRequest Request) : ICommand<ErpPurchaseOrderDto>;

[RequireFeature(ErpFeatures.Purchase)]
public sealed record UpdateErpPurchaseOrderCommand(Guid Id, SaveErpPurchaseOrderRequest Request) : ICommand<ErpPurchaseOrderDto>;

[RequireFeature(ErpFeatures.Purchase)]
public sealed record PlaceErpPurchaseOrderCommand(Guid Id) : ICommand<ErpPurchaseOrderDto>;

[RequireFeature(ErpFeatures.Purchase)]
/// <param name="Request">本次到货的产品和数量；为空时收下全部未到的数量。</param>
public sealed record ReceiveErpPurchaseOrderCommand(Guid Id, ErpDeliveryRequest? Request = null, Guid? RunId = null) : ICommand<ErpPurchaseOrderDto>;

[RequireFeature(ErpFeatures.Purchase)]
public sealed record CloseErpPurchaseOrderCommand(Guid Id) : ICommand<ErpPurchaseOrderDto>;

[RequireFeature(ErpFeatures.Purchase)]
public sealed record ReturnErpPurchaseOrderCommand(Guid Id, ErpReturnRequest Request, Guid? RunId = null) : ICommand<ErpPurchaseOrderDto>;

[RequireFeature(ErpFeatures.Purchase)]
public sealed record CancelErpPurchaseOrderCommand(Guid Id) : ICommand<ErpPurchaseOrderDto>;

[RequireFeature(ErpFeatures.Purchase)]
public sealed record DeleteErpPurchaseOrderCommand(Guid Id) : ICommand<Unit>;

public sealed class SaveErpPurchaseOrderRequestValidator : AbstractValidator<SaveErpPurchaseOrderRequest>
{
    public SaveErpPurchaseOrderRequestValidator()
    {
        RuleFor(x => x.Currency).Currency().OverridePropertyName("currency");
        RuleFor(x => x.Note).Text(2_000, "备注").OverridePropertyName("note");
        this.Lines(x => x.Lines);
    }
}

public sealed class CreateErpPurchaseOrderCommandValidator : AbstractValidator<CreateErpPurchaseOrderCommand>
{
    public CreateErpPurchaseOrderCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveErpPurchaseOrderRequestValidator());
}

public sealed class UpdateErpPurchaseOrderCommandValidator : AbstractValidator<UpdateErpPurchaseOrderCommand>
{
    public UpdateErpPurchaseOrderCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveErpPurchaseOrderRequestValidator());
}

public sealed class ReceiveErpPurchaseOrderCommandValidator : AbstractValidator<ReceiveErpPurchaseOrderCommand>
{
    public ReceiveErpPurchaseOrderCommandValidator() => RuleFor(x => x.Request!).SetValidator(new ErpDeliveryRequestValidator()).When(x => x.Request is not null);
}

public sealed class ReturnErpPurchaseOrderCommandValidator : AbstractValidator<ReturnErpPurchaseOrderCommand>
{
    public ReturnErpPurchaseOrderCommandValidator() => RuleFor(x => x.Request).SetValidator(new ErpReturnRequestValidator());
}

internal sealed class ErpPurchaseOrderHandlers(
    IAppDbContext db,
    ITenantContext tenant,
    ErpRecords records,
    ErpDocuments documents,
    IStockLedger ledger,
    ITextMatcher text,
    IChangeFeed changes,
    TenantCalendar calendar,
    TimeProvider clock) :
    IRequestHandler<ListErpPurchaseOrdersQuery, PagedResult<ErpPurchaseOrderDto>>,
    IRequestHandler<GetErpPurchaseOrderQuery, ErpPurchaseOrderDto>,
    IRequestHandler<CreateErpPurchaseOrderCommand, ErpPurchaseOrderDto>,
    IRequestHandler<UpdateErpPurchaseOrderCommand, ErpPurchaseOrderDto>,
    IRequestHandler<PlaceErpPurchaseOrderCommand, ErpPurchaseOrderDto>,
    IRequestHandler<ReceiveErpPurchaseOrderCommand, ErpPurchaseOrderDto>,
    IRequestHandler<CloseErpPurchaseOrderCommand, ErpPurchaseOrderDto>,
    IRequestHandler<ReturnErpPurchaseOrderCommand, ErpPurchaseOrderDto>,
    IRequestHandler<CancelErpPurchaseOrderCommand, ErpPurchaseOrderDto>,
    IRequestHandler<DeleteErpPurchaseOrderCommand, Unit>
{
    public async Task<PagedResult<ErpPurchaseOrderDto>> HandleAsync(ListErpPurchaseOrdersQuery request, CancellationToken cancellationToken)
    {
        var (page, pageSize) = ErpRecords.Paging(request.Page, request.PageSize);
        var query = db.ErpPurchaseOrders.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            // 订单号或供应商名称包含关键字。
            var numbers = text.WhereContains(db.ErpPurchaseOrders, request.Search, o => o.Number).Select(o => o.Id);
            var suppliers = text.WhereContains(db.CrmCompanies, request.Search, c => c.Name).Select(c => (Guid?)c.Id);
            query = query.Where(o => numbers.Contains(o.Id) || suppliers.Contains(o.SupplierId));
        }

        if (request.Status is { } status)
        {
            query = query.Where(o => o.Status == status);
        }

        if (request.Pending)
        {
            query = query.Where(o => o.Status == ErpPurchaseOrderStatus.Ordered || o.Status == ErpPurchaseOrderStatus.PartiallyReceived);
        }

        if (request.SupplierId is { } supplierId)
        {
            query = query.Where(o => o.SupplierId == supplierId);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<ErpPurchaseOrderDto>(await ErpMapping.PurchaseOrdersAsync(db, items, cancellationToken).ConfigureAwait(false), total, page, pageSize);
    }

    public async Task<ErpPurchaseOrderDto> HandleAsync(GetErpPurchaseOrderQuery request, CancellationToken cancellationToken)
    {
        var order = await db.ErpPurchaseOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == request.Id, cancellationToken).ConfigureAwait(false)
                    ?? throw new NotFoundException("erp_purchase_order", request.Id);
        return (await ErpMapping.PurchaseOrdersAsync(db, [order], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<ErpPurchaseOrderDto> HandleAsync(CreateErpPurchaseOrderCommand request, CancellationToken cancellationToken)
    {
        var order = new ErpPurchaseOrder
        {
            Number = await records.NextNumberAsync("PO", cancellationToken).ConfigureAwait(false),
            CreatedBy = tenant.Actor,
        };
        await ApplyAsync(order, request.Request, cancellationToken).ConfigureAwait(false);
        db.ErpPurchaseOrders.Add(order);
        changes.Publish(ErpRealtime.PurchaseOrder, order.Id, RealtimeChangeKind.Created);
        return (await ErpMapping.PurchaseOrdersAsync(db, [order], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<ErpPurchaseOrderDto> HandleAsync(UpdateErpPurchaseOrderCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        var now = clock.GetUtcNow();
        var version = r.ExpectedUpdatedAt is { } expected ? new DateTimeOffset(expected.UtcTicks - (expected.UtcTicks % 10), TimeSpan.Zero) : (DateTimeOffset?)null;
        var locked = await db.ErpPurchaseOrders
            .Where(o => o.Id == request.Id && o.Status == ErpPurchaseOrderStatus.Draft && (version == null || o.UpdatedAt == version))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, o => o.Status), cancellationToken).ConfigureAwait(false);
        if (locked == 0)
        {
            var current = await records.PurchaseOrderAsync(request.Id, cancellationToken).ConfigureAwait(false);
            throw current.Status != ErpPurchaseOrderStatus.Draft
                ? StatusChanged(current.Status, "修改")
                : new ConflictException("erp_purchase_order.modified", "订单刚被其他人修改过，已刷新为最新内容，请确认后再保存。");
        }

        var order = await records.PurchaseOrderAsync(request.Id, cancellationToken).ConfigureAwait(false);
        await ApplyAsync(order, r, cancellationToken).ConfigureAwait(false);
        order.UpdatedAt = now;
        changes.Publish(ErpRealtime.PurchaseOrder, order.Id, RealtimeChangeKind.Updated);
        return (await ErpMapping.PurchaseOrdersAsync(db, [order], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <summary>
    /// 下单：订单发给供应商后不能再修改，只能收货或取消。
    /// </summary>
    public async Task<ErpPurchaseOrderDto> HandleAsync(PlaceErpPurchaseOrderCommand request, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var order = await LockAsync(request.Id, [ErpPurchaseOrderStatus.Draft], "下单", cancellationToken).ConfigureAwait(false);
        order.Place(now);
        order.UpdatedAt = now;
        changes.Publish(ErpRealtime.PurchaseOrder, order.Id, RealtimeChangeKind.Updated);
        return (await ErpMapping.PurchaseOrdersAsync(db, [order], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <summary>
    /// 到货（可以只到一部分）：管库存的产品入库，按采购单价重算移动加权平均成本，生成一张收货单；
    /// 全部收完时订单变为已到货，否则为部分到货。
    /// </summary>
    public async Task<ErpPurchaseOrderDto> HandleAsync(ReceiveErpPurchaseOrderCommand request, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var order = await LockAsync(request.Id, [ErpPurchaseOrderStatus.Ordered, ErpPurchaseOrderStatus.PartiallyReceived], "收货", cancellationToken).ConfigureAwait(false);
        var products = await records.LockedProductsAsync(order.Lines.Select(l => l.ProductId), cancellationToken).ConfigureAwait(false);
        var quantities = ErpDocuments.Quantities(order.Lines, request.Request?.Lines, l => l.Remaining, products, "收货");
        var document = await documents.CreateAsync(
            ErpStockDocumentKind.Receipt, null, order, order.WarehouseId, request.Request?.Note ?? string.Empty, request.RunId, cancellationToken).ConfigureAwait(false);
        foreach (var (line, quantity) in ErpDocuments.Ordered(order.Lines, quantities))
        {
            var product = products[line.ProductId];
            var unitCost = ErpDocuments.PurchaseCost(order, line, product);
            if (product.TrackInventory)
            {
                var moved = await records.MoveStockAsync(
                        line.ProductId, order.WarehouseId, ErpStockMovementKind.Purchase, quantity, 0, false, $"采购订单 {order.Number} 到货（{document.Number}）",
                        null, order.Id, request.RunId, cancellationToken, unitCost, document.Id)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"采购订单 {order.Number} 入库失败。");
                unitCost = moved.UnitCost;
            }

            ErpDocuments.AddLine(document, line, quantity, line.Delivered, unitCost ?? 0, product);
        }

        order.Receive(quantities, now);
        order.UpdatedAt = now;
        return await PublishAsync(order, ErpEventKeys.PurchaseOrderReceived, document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 结束收货：部分到货的订单剩下的不再等，订单变为已到货。
    /// </summary>
    public async Task<ErpPurchaseOrderDto> HandleAsync(CloseErpPurchaseOrderCommand request, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var order = await LockAsync(request.Id, [ErpPurchaseOrderStatus.PartiallyReceived], "结束收货", cancellationToken).ConfigureAwait(false);
        order.CloseReceiving(now);
        order.UpdatedAt = now;
        changes.Publish(ErpRealtime.PurchaseOrder, order.Id, RealtimeChangeKind.Updated);
        return (await ErpMapping.PurchaseOrdersAsync(db, [order], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <summary>
    /// 退货给供应商：从仓库（默认订单的仓库）出库，只能动用可用数量（已预留给销售订单的不动），按当时的平均成本记出库，
    /// 生成一张退货单。订单状态不变，退货数量记在订单行上。
    /// </summary>
    public async Task<ErpPurchaseOrderDto> HandleAsync(ReturnErpPurchaseOrderCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        var order = await LockAsync(request.Id, [ErpPurchaseOrderStatus.PartiallyReceived, ErpPurchaseOrderStatus.Received], "退货", cancellationToken).ConfigureAwait(false);
        var warehouse = await records.WarehouseAsync(r.WarehouseId ?? order.WarehouseId, cancellationToken).ConfigureAwait(false);
        var products = await records.LockedProductsAsync(order.Lines.Select(l => l.ProductId), cancellationToken).ConfigureAwait(false);
        var quantities = ErpDocuments.Quantities(order.Lines, r.Lines, l => l.Returnable, products, "退");
        var document = await documents.CreateAsync(ErpStockDocumentKind.PurchaseReturn, null, order, warehouse.Id, r.Note, request.RunId, cancellationToken).ConfigureAwait(false);
        var shortages = new List<string>();
        var decreases = new Dictionary<Guid, decimal>();
        foreach (var (line, quantity) in ErpDocuments.Ordered(order.Lines, quantities))
        {
            var product = products[line.ProductId];
            var unitCost = ErpDocuments.PurchaseCost(order, line, product) ?? 0;
            if (product.TrackInventory)
            {
                var moved = await records.MoveStockAsync(
                        line.ProductId, warehouse.Id, ErpStockMovementKind.PurchaseReturn, -quantity, 0, true, $"采购订单 {order.Number} 退货（{document.Number}）",
                        null, order.Id, request.RunId, cancellationToken, documentId: document.Id)
                    .ConfigureAwait(false);
                if (moved is null)
                {
                    var current = await ledger.LockAsync(line.ProductId, warehouse.Id, cancellationToken).ConfigureAwait(false);
                    shortages.Add(ErpRecords.Shortage(product, quantity, current.Available));
                    continue;
                }

                unitCost = moved.UnitCost;
                decreases[line.ProductId] = quantity;
            }

            ErpDocuments.AddLine(document, line, quantity, line.Returned, unitCost, product);
        }

        if (shortages.Count > 0)
        {
            throw new ConflictException("erp.insufficient_stock", $"「{warehouse.Name}」可用库存不足，无法退货（已预留给销售订单的不能退）：{string.Join("；", shortages)}。");
        }

        order.Return(quantities);
        order.UpdatedAt = clock.GetUtcNow();
        await records.CheckLowStockAsync(decreases, cancellationToken).ConfigureAwait(false);
        return await PublishAsync(order, ErpEventKeys.PurchaseOrderReturned, document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 推送订单和单据变化，发布带 order 与 document（本次的收货单或退货单）的事件。
    /// </summary>
    private async Task<ErpPurchaseOrderDto> PublishAsync(ErpPurchaseOrder order, string eventType, ErpStockDocument document, CancellationToken cancellationToken)
    {
        changes.Publish(ErpRealtime.PurchaseOrder, order.Id, RealtimeChangeKind.Updated);
        changes.Publish(ErpRealtime.Document, document.Id, RealtimeChangeKind.Created);

        // 订单 DTO 里的已到货金额由单据汇总，先保存本次的单据再映射。
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var dto = (await ErpMapping.PurchaseOrdersAsync(db, [order], cancellationToken).ConfigureAwait(false))[0];
        await records.PublishAsync(eventType, order.Id, async () => new Dictionary<string, object?>
        {
            ["order"] = dto,
            ["document"] = (await ErpMapping.DocumentsAsync(db, [document], cancellationToken).ConfigureAwait(false))[0],
        }, cancellationToken).ConfigureAwait(false);
        return dto;
    }

    public async Task<ErpPurchaseOrderDto> HandleAsync(CancelErpPurchaseOrderCommand request, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var order = await LockAsync(request.Id, [ErpPurchaseOrderStatus.Draft, ErpPurchaseOrderStatus.Ordered], "取消", cancellationToken).ConfigureAwait(false);
        order.Cancel(now);
        order.UpdatedAt = now;
        changes.Publish(ErpRealtime.PurchaseOrder, order.Id, RealtimeChangeKind.Updated);
        return (await ErpMapping.PurchaseOrdersAsync(db, [order], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<Unit> HandleAsync(DeleteErpPurchaseOrderCommand request, CancellationToken cancellationToken)
    {
        var order = await LockAsync(request.Id, [ErpPurchaseOrderStatus.Draft, ErpPurchaseOrderStatus.Cancelled], "删除", cancellationToken).ConfigureAwait(false);
        db.ErpPurchaseOrders.Remove(order);
        changes.Publish(ErpRealtime.PurchaseOrder, order.Id, RealtimeChangeKind.Deleted);
        return Unit.Value;
    }

    /// <summary>
    /// 以"状态仍是允许的那几种"为条件先锁住订单行（不改任何值），再读取最新内容：并发的两次收货只有一次成功。
    /// </summary>
    private async Task<ErpPurchaseOrder> LockAsync(Guid id, ErpPurchaseOrderStatus[] allowed, string action, CancellationToken cancellationToken)
    {
        var locked = await db.ErpPurchaseOrders.Where(o => o.Id == id && allowed.Contains(o.Status))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, o => o.Status), cancellationToken).ConfigureAwait(false);
        var order = await records.PurchaseOrderAsync(id, cancellationToken).ConfigureAwait(false);
        return locked == 0 ? throw StatusChanged(order.Status, action) : order;
    }

    private static ConflictException StatusChanged(ErpPurchaseOrderStatus status, string action)
        => new("erp_purchase_order.status_changed", $"采购订单当前是「{ErpLabels.Of(status)}」，不能{action}。已刷新为最新状态。");

    private async Task ApplyAsync(ErpPurchaseOrder order, SaveErpPurchaseOrderRequest r, CancellationToken cancellationToken)
    {
        // 下单日期省略时取今天，预计到货日期按实际保存的下单日期检查。
        var orderDate = r.OrderDate ?? await calendar.TodayAsync(cancellationToken).ConfigureAwait(false);
        if (r.ExpectedDate < orderDate)
        {
            throw new ValidationFailedException(new Dictionary<string, string[]> { ["expectedDate"] = ["预计到货日期不能早于下单日期。"] });
        }

        if (!await db.CrmCompanies.AnyAsync(c => c.Id == r.SupplierId, cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException("crm_company", r.SupplierId);
        }

        var warehouse = await records.ResolveWarehouseAsync(r.WarehouseId, cancellationToken).ConfigureAwait(false);
        order.Currency = r.Currency.Trim().ToUpperInvariant();
        var (lines, _) = await records.BuildLinesAsync(r.Lines, order.Currency, p => p.Cost, cancellationToken).ConfigureAwait(false);
        order.SupplierId = r.SupplierId;
        order.WarehouseId = warehouse.Id;
        order.OrderDate = orderDate;
        order.ExpectedDate = r.ExpectedDate;
        order.Note = r.Note.Trim();
        order.SetLines(lines);
    }
}
