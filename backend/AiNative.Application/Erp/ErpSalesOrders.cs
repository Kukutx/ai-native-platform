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

/// <param name="Pending">只看还没发完的订单（已确认和部分发货）。</param>
[RequireFeature(ErpFeatures.View)]
public sealed record ListErpSalesOrdersQuery(
    string? Search = null,
    ErpSalesOrderStatus? Status = null,
    Guid? CompanyId = null,
    Guid? DealId = null,
    Guid? OwnerUserId = null,
    int Page = 1,
    int PageSize = 50,
    bool Pending = false) : IQuery<PagedResult<ErpSalesOrderDto>>;

[RequireFeature(ErpFeatures.View)]
public sealed record GetErpSalesOrderQuery(Guid Id) : IQuery<ErpSalesOrderDto>;

[RequireFeature(ErpFeatures.Sales)]
/// <param name="Confirm">建好后立即确认（预留库存），库存不够时整单不建。</param>
public sealed record CreateErpSalesOrderCommand(SaveErpSalesOrderRequest Request, bool Confirm = false) : ICommand<ErpSalesOrderDto>;

[RequireFeature(ErpFeatures.Sales)]
public sealed record UpdateErpSalesOrderCommand(Guid Id, SaveErpSalesOrderRequest Request) : ICommand<ErpSalesOrderDto>;

[RequireFeature(ErpFeatures.Sales)]
public sealed record ConfirmErpSalesOrderCommand(Guid Id) : ICommand<ErpSalesOrderDto>;

[RequireFeature(ErpFeatures.Sales)]
/// <param name="Request">本次发货的产品和数量；为空时发出全部未发的数量。</param>
public sealed record ShipErpSalesOrderCommand(Guid Id, ErpDeliveryRequest? Request = null, Guid? RunId = null) : ICommand<ErpSalesOrderDto>;

[RequireFeature(ErpFeatures.Sales)]
public sealed record CloseErpSalesOrderCommand(Guid Id) : ICommand<ErpSalesOrderDto>;

[RequireFeature(ErpFeatures.Sales)]
public sealed record ReturnErpSalesOrderCommand(Guid Id, ErpReturnRequest Request, Guid? RunId = null) : ICommand<ErpSalesOrderDto>;

[RequireFeature(ErpFeatures.Sales)]
public sealed record CancelErpSalesOrderCommand(Guid Id) : ICommand<ErpSalesOrderDto>;

[RequireFeature(ErpFeatures.Sales)]
public sealed record DeleteErpSalesOrderCommand(Guid Id) : ICommand<Unit>;

public sealed class SaveErpSalesOrderRequestValidator : AbstractValidator<SaveErpSalesOrderRequest>
{
    public SaveErpSalesOrderRequestValidator()
    {
        RuleFor(x => x.Currency!).Currency().When(x => x.Currency is not null).OverridePropertyName("currency");
        RuleFor(x => x.Note).Text(2_000, "备注").OverridePropertyName("note");
        this.Lines(x => x.Lines);
    }
}

public sealed class CreateErpSalesOrderCommandValidator : AbstractValidator<CreateErpSalesOrderCommand>
{
    public CreateErpSalesOrderCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveErpSalesOrderRequestValidator());
}

public sealed class UpdateErpSalesOrderCommandValidator : AbstractValidator<UpdateErpSalesOrderCommand>
{
    public UpdateErpSalesOrderCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveErpSalesOrderRequestValidator());
}

public sealed class ShipErpSalesOrderCommandValidator : AbstractValidator<ShipErpSalesOrderCommand>
{
    public ShipErpSalesOrderCommandValidator() => RuleFor(x => x.Request!).SetValidator(new ErpDeliveryRequestValidator()).When(x => x.Request is not null);
}

public sealed class ReturnErpSalesOrderCommandValidator : AbstractValidator<ReturnErpSalesOrderCommand>
{
    public ReturnErpSalesOrderCommandValidator() => RuleFor(x => x.Request).SetValidator(new ErpReturnRequestValidator());
}

public static class ErpLabels
{
    public static string Of(ErpSalesOrderStatus status) => status switch
    {
        ErpSalesOrderStatus.Draft => "草稿",
        ErpSalesOrderStatus.Confirmed => "已确认",
        ErpSalesOrderStatus.PartiallyShipped => "部分发货",
        ErpSalesOrderStatus.Shipped => "已发货",
        _ => "已取消",
    };

    public static string Of(ErpPurchaseOrderStatus status) => status switch
    {
        ErpPurchaseOrderStatus.Draft => "草稿",
        ErpPurchaseOrderStatus.Ordered => "已下单",
        ErpPurchaseOrderStatus.PartiallyReceived => "部分到货",
        ErpPurchaseOrderStatus.Received => "已到货",
        _ => "已取消",
    };
}

internal sealed class ErpSalesOrderHandlers(
    IAppDbContext db,
    ITenantContext tenant,
    ErpRecords records,
    ErpDocuments documents,
    IStockLedger ledger,
    ITextMatcher text,
    IChangeFeed changes,
    TenantCalendar calendar,
    TimeProvider clock) :
    IRequestHandler<ListErpSalesOrdersQuery, PagedResult<ErpSalesOrderDto>>,
    IRequestHandler<GetErpSalesOrderQuery, ErpSalesOrderDto>,
    IRequestHandler<CreateErpSalesOrderCommand, ErpSalesOrderDto>,
    IRequestHandler<UpdateErpSalesOrderCommand, ErpSalesOrderDto>,
    IRequestHandler<ConfirmErpSalesOrderCommand, ErpSalesOrderDto>,
    IRequestHandler<ShipErpSalesOrderCommand, ErpSalesOrderDto>,
    IRequestHandler<CloseErpSalesOrderCommand, ErpSalesOrderDto>,
    IRequestHandler<ReturnErpSalesOrderCommand, ErpSalesOrderDto>,
    IRequestHandler<CancelErpSalesOrderCommand, ErpSalesOrderDto>,
    IRequestHandler<DeleteErpSalesOrderCommand, Unit>
{
    public async Task<PagedResult<ErpSalesOrderDto>> HandleAsync(ListErpSalesOrdersQuery request, CancellationToken cancellationToken)
    {
        var (page, pageSize) = ErpRecords.Paging(request.Page, request.PageSize);
        var query = db.ErpSalesOrders.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            // 订单号或客户名称包含关键字。
            var numbers = text.WhereContains(db.ErpSalesOrders, request.Search, o => o.Number).Select(o => o.Id);
            var companies = text.WhereContains(db.CrmCompanies, request.Search, c => c.Name).Select(c => (Guid?)c.Id);
            query = query.Where(o => numbers.Contains(o.Id) || companies.Contains(o.CompanyId));
        }

        if (request.Status is { } status)
        {
            query = query.Where(o => o.Status == status);
        }

        if (request.Pending)
        {
            query = query.Where(o => o.Status == ErpSalesOrderStatus.Confirmed || o.Status == ErpSalesOrderStatus.PartiallyShipped);
        }

        if (request.CompanyId is { } companyId)
        {
            query = query.Where(o => o.CompanyId == companyId);
        }

        if (request.DealId is { } dealId)
        {
            query = query.Where(o => o.DealId == dealId);
        }

        if (request.OwnerUserId is { } owner)
        {
            query = query.Where(o => o.OwnerUserId == owner);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<ErpSalesOrderDto>(await ErpMapping.SalesOrdersAsync(db, items, cancellationToken).ConfigureAwait(false), total, page, pageSize);
    }

    public async Task<ErpSalesOrderDto> HandleAsync(GetErpSalesOrderQuery request, CancellationToken cancellationToken)
    {
        var order = await db.ErpSalesOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == request.Id, cancellationToken).ConfigureAwait(false)
                    ?? throw new NotFoundException("erp_sales_order", request.Id);
        return (await ErpMapping.SalesOrdersAsync(db, [order], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<ErpSalesOrderDto> HandleAsync(CreateErpSalesOrderCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        var owner = r.OwnerUserId ?? (tenant.Actor.IsUser ? tenant.Actor.Id : null);
        await records.EnsureOwnerAsync(owner, cancellationToken).ConfigureAwait(false);
        var order = new ErpSalesOrder
        {
            Number = await records.NextNumberAsync("SO", cancellationToken).ConfigureAwait(false),
            CreatedBy = tenant.Actor,
            OwnerUserId = owner,
        };
        await ApplyAsync(order, r, cancellationToken).ConfigureAwait(false);
        db.ErpSalesOrders.Add(order);
        changes.Publish(ErpRealtime.SalesOrder, order.Id, RealtimeChangeKind.Created);
        if (request.Confirm)
        {
            // 新订单别人还看不到，不需要先锁。
            return await ConfirmAsync(order, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }

        return (await ErpMapping.SalesOrdersAsync(db, [order], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<ErpSalesOrderDto> HandleAsync(UpdateErpSalesOrderCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        var now = clock.GetUtcNow();
        var version = r.ExpectedUpdatedAt is { } expected ? new DateTimeOffset(expected.UtcTicks - (expected.UtcTicks % 10), TimeSpan.Zero) : (DateTimeOffset?)null;
        var locked = await db.ErpSalesOrders
            .Where(o => o.Id == request.Id && o.Status == ErpSalesOrderStatus.Draft && (version == null || o.UpdatedAt == version))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, o => o.Status), cancellationToken).ConfigureAwait(false);
        if (locked == 0)
        {
            var current = await records.SalesOrderAsync(request.Id, cancellationToken).ConfigureAwait(false);
            throw current.Status != ErpSalesOrderStatus.Draft
                ? StatusChanged(current.Status, "修改")
                : new ConflictException("erp_sales_order.modified", "订单刚被其他人修改过，已刷新为最新内容，请确认后再保存。");
        }

        var order = await records.SalesOrderAsync(request.Id, cancellationToken).ConfigureAwait(false);
        await records.EnsureOwnerAsync(r.OwnerUserId, cancellationToken).ConfigureAwait(false);
        order.OwnerUserId = r.OwnerUserId;
        await ApplyAsync(order, r, cancellationToken).ConfigureAwait(false);
        order.UpdatedAt = now;
        changes.Publish(ErpRealtime.SalesOrder, order.Id, RealtimeChangeKind.Updated);
        return (await ErpMapping.SalesOrdersAsync(db, [order], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <summary>
    /// 确认订单：按订单行预留库存（只占可用数量）。任何一行不够都整单失败，提示所有缺货的产品。
    /// </summary>
    public async Task<ErpSalesOrderDto> HandleAsync(ConfirmErpSalesOrderCommand request, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var order = await LockAsync(request.Id, [ErpSalesOrderStatus.Draft], "确认", cancellationToken).ConfigureAwait(false);
        return await ConfirmAsync(order, now, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ErpSalesOrderDto> ConfirmAsync(ErpSalesOrder order, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var products = await TrackedProductsAsync(order, cancellationToken).ConfigureAwait(false);
        var shortages = new List<string>();
        var decreases = new Dictionary<Guid, decimal>();
        foreach (var line in TrackedLines(order, products))
        {
            var reserved = await ledger.TryChangeAsync(line.ProductId, order.WarehouseId, 0, line.Quantity, true, cancellationToken).ConfigureAwait(false);
            if (reserved is null)
            {
                var current = await ledger.LockAsync(line.ProductId, order.WarehouseId, cancellationToken).ConfigureAwait(false);
                shortages.Add(ErpRecords.Shortage(products[line.ProductId], line.Quantity, current.Available));
                continue;
            }

            decreases[line.ProductId] = line.Quantity;
            changes.Publish(ErpRealtime.Stock, line.ProductId, RealtimeChangeKind.Updated);
        }

        if (shortages.Count > 0)
        {
            var warehouse = await db.ErpWarehouses.AsNoTracking().Where(w => w.Id == order.WarehouseId).Select(w => w.Name).FirstAsync(cancellationToken).ConfigureAwait(false);
            throw new ConflictException("erp.insufficient_stock", $"「{warehouse}」库存不足，无法确认：{string.Join("；", shortages)}。");
        }

        order.Confirm(now);
        order.UpdatedAt = now;
        await records.CheckLowStockAsync(decreases, cancellationToken).ConfigureAwait(false);
        return await PublishAsync(order, ErpEventKeys.SalesOrderConfirmed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 发货（可以只发一部分）：管库存的产品扣减在库数量并释放对应的预留，按出库时的平均成本记出库流水，
    /// 生成一张发货单；全部发完时订单变为已发货，否则为部分发货。
    /// </summary>
    public async Task<ErpSalesOrderDto> HandleAsync(ShipErpSalesOrderCommand request, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var order = await LockAsync(request.Id, [ErpSalesOrderStatus.Confirmed, ErpSalesOrderStatus.PartiallyShipped], "发货", cancellationToken).ConfigureAwait(false);
        var products = await records.LockedProductsAsync(order.Lines.Select(l => l.ProductId), cancellationToken).ConfigureAwait(false);
        var quantities = ErpDocuments.Quantities(order.Lines, request.Request?.Lines, l => l.Remaining, products, "发货");
        var document = await documents.CreateAsync(
            ErpStockDocumentKind.Shipment, order, null, order.WarehouseId, request.Request?.Note ?? string.Empty, request.RunId, cancellationToken).ConfigureAwait(false);
        var shortages = new List<string>();
        foreach (var (line, quantity) in ErpDocuments.Ordered(order.Lines, quantities))
        {
            var product = products[line.ProductId];
            var unitCost = product.Cost ?? 0;
            if (product.TrackInventory)
            {
                var moved = await records.MoveStockAsync(
                        line.ProductId, order.WarehouseId, ErpStockMovementKind.Sale, -quantity, -quantity, false, $"销售订单 {order.Number} 发货（{document.Number}）",
                        order.Id, null, request.RunId, cancellationToken, documentId: document.Id)
                    .ConfigureAwait(false);
                if (moved is null)
                {
                    var current = await ledger.LockAsync(line.ProductId, order.WarehouseId, cancellationToken).ConfigureAwait(false);
                    shortages.Add(ErpRecords.Shortage(product, quantity, current.OnHand));
                    continue;
                }

                unitCost = moved.UnitCost;
            }

            ErpDocuments.AddLine(document, line, quantity, line.Delivered, unitCost, product);
        }

        if (shortages.Count > 0)
        {
            throw new ConflictException("erp.insufficient_stock", $"在库数量不足，无法发货（可能盘点时少了货）：{string.Join("；", shortages)}。请先核实库存。");
        }

        order.Ship(quantities, now);
        order.UpdatedAt = now;
        return await PublishAsync(order, ErpEventKeys.SalesOrderShipped, document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 结束发货：部分发货的订单不再发剩下的数量，释放剩下的预留，订单变为已发货。
    /// </summary>
    public async Task<ErpSalesOrderDto> HandleAsync(CloseErpSalesOrderCommand request, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var order = await LockAsync(request.Id, [ErpSalesOrderStatus.PartiallyShipped], "结束发货", cancellationToken).ConfigureAwait(false);
        await ReleaseAsync(order, cancellationToken).ConfigureAwait(false);
        order.CloseShipping(now);
        order.UpdatedAt = now;
        changes.Publish(ErpRealtime.SalesOrder, order.Id, RealtimeChangeKind.Updated);
        return (await ErpMapping.SalesOrdersAsync(db, [order], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <summary>
    /// 客户退货：退回的货入库（默认回到订单的仓库），成本按这张订单发货时的平均成本冲回，生成一张退货单。
    /// 订单状态不变，退货数量记在订单行上。
    /// </summary>
    public async Task<ErpSalesOrderDto> HandleAsync(ReturnErpSalesOrderCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        var order = await LockAsync(request.Id, [ErpSalesOrderStatus.PartiallyShipped, ErpSalesOrderStatus.Shipped], "退货", cancellationToken).ConfigureAwait(false);
        var warehouseId = r.WarehouseId is { } id ? (await records.WarehouseAsync(id, cancellationToken).ConfigureAwait(false)).Id : order.WarehouseId;
        var products = await records.LockedProductsAsync(order.Lines.Select(l => l.ProductId), cancellationToken).ConfigureAwait(false);
        var quantities = ErpDocuments.Quantities(order.Lines, r.Lines, l => l.Returnable, products, "退");

        // 这张订单每个产品发出去（减去已退回）的数量和成本，退回时按它们的平均值冲回，毛利与发货时的成本一致。
        var settled = await db.ErpStockDocuments.AsNoTracking()
            .Where(d => d.SalesOrderId == order.Id)
            .SelectMany(d => d.Lines, (d, l) => new { d.Kind, l.ProductId, l.Quantity, l.CostAmount })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var document = await documents.CreateAsync(ErpStockDocumentKind.SalesReturn, order, null, warehouseId, r.Note, request.RunId, cancellationToken).ConfigureAwait(false);
        foreach (var (line, quantity) in ErpDocuments.Ordered(order.Lines, quantities))
        {
            var product = products[line.ProductId];
            var shipped = settled.Where(x => x.ProductId == line.ProductId).ToList();
            var netQuantity = shipped.Sum(x => ErpStockDocument.Sign(x.Kind) * x.Quantity);
            var netCost = shipped.Sum(x => ErpStockDocument.Sign(x.Kind) * x.CostAmount);
            var unitCost = netQuantity > 0 ? ErpCosting.Unit(netCost / netQuantity) : product.Cost ?? 0;
            if (product.TrackInventory)
            {
                _ = await records.MoveStockAsync(
                        line.ProductId, warehouseId, ErpStockMovementKind.SalesReturn, quantity, 0, false, $"销售订单 {order.Number} 退货（{document.Number}）",
                        order.Id, null, request.RunId, cancellationToken, unitCost, document.Id)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"销售订单 {order.Number} 退货入库失败。");
            }

            ErpDocuments.AddLine(document, line, quantity, line.Returned, unitCost, product);
        }

        order.Return(quantities);
        order.UpdatedAt = clock.GetUtcNow();
        return await PublishAsync(order, ErpEventKeys.SalesOrderReturned, document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 取消：草稿直接取消；已确认的订单释放预留的库存。已经发货的订单不能取消（部分发货的可以结束发货）。
    /// </summary>
    public async Task<ErpSalesOrderDto> HandleAsync(CancelErpSalesOrderCommand request, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var order = await LockAsync(request.Id, [ErpSalesOrderStatus.Draft, ErpSalesOrderStatus.Confirmed], "取消", cancellationToken).ConfigureAwait(false);
        if (order.Status == ErpSalesOrderStatus.Confirmed)
        {
            await ReleaseAsync(order, cancellationToken).ConfigureAwait(false);
        }

        order.Cancel(now);
        order.UpdatedAt = now;
        changes.Publish(ErpRealtime.SalesOrder, order.Id, RealtimeChangeKind.Updated);
        return (await ErpMapping.SalesOrdersAsync(db, [order], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <summary>
    /// 只能删除草稿或已取消的订单（它们没有动过在库数量）。
    /// </summary>
    public async Task<Unit> HandleAsync(DeleteErpSalesOrderCommand request, CancellationToken cancellationToken)
    {
        var order = await LockAsync(request.Id, [ErpSalesOrderStatus.Draft, ErpSalesOrderStatus.Cancelled], "删除", cancellationToken).ConfigureAwait(false);
        db.ErpSalesOrders.Remove(order);
        changes.Publish(ErpRealtime.SalesOrder, order.Id, RealtimeChangeKind.Deleted);
        return Unit.Value;
    }

    /// <summary>
    /// 以"状态仍是允许的那几种"为条件先锁住订单行（不改任何值），再读取最新内容：并发的另一次操作会冲突而不是重复扣库存。
    /// </summary>
    private async Task<ErpSalesOrder> LockAsync(Guid id, ErpSalesOrderStatus[] allowed, string action, CancellationToken cancellationToken)
    {
        var locked = await db.ErpSalesOrders.Where(o => o.Id == id && allowed.Contains(o.Status))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, o => o.Status), cancellationToken).ConfigureAwait(false);
        var order = await records.SalesOrderAsync(id, cancellationToken).ConfigureAwait(false);
        return locked == 0 ? throw StatusChanged(order.Status, action) : order;
    }

    /// <summary>
    /// 释放订单还没发出的那部分预留。
    /// </summary>
    private async Task ReleaseAsync(ErpSalesOrder order, CancellationToken cancellationToken)
    {
        var products = await TrackedProductsAsync(order, cancellationToken).ConfigureAwait(false);
        foreach (var line in TrackedLines(order, products).Where(l => l.Remaining > 0))
        {
            _ = await ledger.TryChangeAsync(line.ProductId, order.WarehouseId, 0, -line.Remaining, false, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"销售订单 {order.Number} 的预留数量与库存不一致。");
            changes.Publish(ErpRealtime.Stock, line.ProductId, RealtimeChangeKind.Updated);
        }
    }

    private static ConflictException StatusChanged(ErpSalesOrderStatus status, string action)
        => new("erp_sales_order.status_changed", $"订单当前是「{ErpLabels.Of(status)}」，不能{action}。已刷新为最新状态。");

    private Task<Dictionary<Guid, ErpProduct>> TrackedProductsAsync(ErpSalesOrder order, CancellationToken cancellationToken)
        => records.TrackedProductsAsync(order.Lines.Select(l => l.ProductId), cancellationToken);

    /// <summary>
    /// 管库存的订单行，按产品 ID 排序：并发订单按同样的顺序锁库存行，不会互相等待成死锁。
    /// </summary>
    private static IEnumerable<ErpOrderLine> TrackedLines(ErpSalesOrder order, Dictionary<Guid, ErpProduct> products)
        => order.Lines.Where(l => products.ContainsKey(l.ProductId)).OrderBy(l => l.ProductId);

    /// <summary>
    /// 推送订单变化并发布事件；有单据时载荷带上 document（本次的发货单或退货单）。
    /// </summary>
    private async Task<ErpSalesOrderDto> PublishAsync(ErpSalesOrder order, string eventType, CancellationToken cancellationToken, ErpStockDocument? document = null)
    {
        changes.Publish(ErpRealtime.SalesOrder, order.Id, RealtimeChangeKind.Updated);
        if (document is not null)
        {
            changes.Publish(ErpRealtime.Document, document.Id, RealtimeChangeKind.Created);
        }

        // 订单 DTO 里的已发货金额、毛利由单据汇总，先保存本次的单据再映射。
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var dto = (await ErpMapping.SalesOrdersAsync(db, [order], cancellationToken).ConfigureAwait(false))[0];
        await records.PublishAsync(eventType, order.Id, async () =>
        {
            var payload = new Dictionary<string, object?> { ["order"] = dto };
            if (document is not null)
            {
                payload["document"] = (await ErpMapping.DocumentsAsync(db, [document], cancellationToken).ConfigureAwait(false))[0];
            }

            return payload;
        }, cancellationToken).ConfigureAwait(false);
        return dto;
    }

    private Task<ErpSalesOrderDto> PublishAsync(ErpSalesOrder order, string eventType, ErpStockDocument document, CancellationToken cancellationToken)
        => PublishAsync(order, eventType, cancellationToken, document);

    /// <summary>
    /// 写入客户、仓库与订单行。来自商机时带出客户、联系人和货币；只选了联系人时带上其所属客户；联系人与客户对不上时拒绝。
    /// </summary>
    private async Task ApplyAsync(ErpSalesOrder order, SaveErpSalesOrderRequest r, CancellationToken cancellationToken)
    {
        var companyId = r.CompanyId;
        var contactId = r.ContactId;
        var currency = r.Currency;
        if (r.DealId is { } dealId)
        {
            var deal = await db.CrmDeals.AsNoTracking().Where(d => d.Id == dealId).Select(d => new { d.CompanyId, d.ContactId, d.Currency })
                           .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                       ?? throw new NotFoundException("crm_deal", dealId);
            if (companyId is not null && deal.CompanyId is not null && companyId != deal.CompanyId)
            {
                throw new ValidationFailedException(new Dictionary<string, string[]> { ["dealId"] = ["商机不属于所选客户。"] });
            }

            // 只给了商机时沿用商机的客户和联系人；另选了联系人时，联系人仍要属于商机的客户（下面检查）。
            if (companyId is null && contactId is null)
            {
                contactId = deal.ContactId;
            }

            companyId ??= deal.CompanyId;
            currency ??= deal.Currency;
        }

        if (contactId is { } id)
        {
            var contact = await db.CrmContacts.AsNoTracking().Where(c => c.Id == id).Select(c => new { c.CompanyId })
                              .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                          ?? throw new NotFoundException("crm_contact", id);
            if (companyId is not null && contact.CompanyId is not null && contact.CompanyId != companyId)
            {
                throw new ValidationFailedException(new Dictionary<string, string[]> { ["contactId"] = ["联系人不属于所选客户。"] });
            }

            companyId ??= contact.CompanyId;
        }

        if (companyId is { } company && !await db.CrmCompanies.AnyAsync(c => c.Id == company, cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException("crm_company", company);
        }

        var warehouse = await records.ResolveWarehouseAsync(r.WarehouseId, cancellationToken).ConfigureAwait(false);
        order.Currency = (currency ?? "CNY").Trim().ToUpperInvariant();
        var (lines, _) = await records.BuildLinesAsync(r.Lines, order.Currency, p => p.Price, cancellationToken).ConfigureAwait(false);
        order.CompanyId = companyId;
        order.ContactId = contactId;
        order.DealId = r.DealId;
        order.WarehouseId = warehouse.Id;
        order.OrderDate = r.OrderDate ?? await calendar.TodayAsync(cancellationToken).ConfigureAwait(false);
        order.Note = r.Note.Trim();
        order.SetLines(lines);
    }
}
