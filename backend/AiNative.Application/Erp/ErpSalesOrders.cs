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
public sealed record ListErpSalesOrdersQuery(
    string? Search = null,
    ErpSalesOrderStatus? Status = null,
    Guid? CompanyId = null,
    Guid? DealId = null,
    Guid? OwnerUserId = null,
    int Page = 1,
    int PageSize = 50) : IQuery<PagedResult<ErpSalesOrderDto>>;

[RequireFeature(ErpFeatures.View)]
public sealed record GetErpSalesOrderQuery(Guid Id) : IQuery<ErpSalesOrderDto>;

/// <summary>
/// 按订单号（例如 SO-0001，不区分大小写）查找。
/// </summary>
[RequireFeature(ErpFeatures.View)]
public sealed record FindErpSalesOrderQuery(string Number) : IQuery<ErpSalesOrderDto>;

[RequireFeature(ErpFeatures.Sales)]
/// <param name="Confirm">建好后立即确认（预留库存），库存不够时整单不建。</param>
public sealed record CreateErpSalesOrderCommand(SaveErpSalesOrderRequest Request, bool Confirm = false) : ICommand<ErpSalesOrderDto>;

[RequireFeature(ErpFeatures.Sales)]
public sealed record UpdateErpSalesOrderCommand(Guid Id, SaveErpSalesOrderRequest Request) : ICommand<ErpSalesOrderDto>;

[RequireFeature(ErpFeatures.Sales)]
public sealed record ConfirmErpSalesOrderCommand(Guid Id) : ICommand<ErpSalesOrderDto>;

[RequireFeature(ErpFeatures.Sales)]
public sealed record ShipErpSalesOrderCommand(Guid Id, Guid? RunId = null) : ICommand<ErpSalesOrderDto>;

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

public static class ErpLabels
{
    public static string Of(ErpSalesOrderStatus status) => status switch
    {
        ErpSalesOrderStatus.Draft => "草稿",
        ErpSalesOrderStatus.Confirmed => "已确认",
        ErpSalesOrderStatus.Shipped => "已发货",
        _ => "已取消",
    };

    public static string Of(ErpPurchaseOrderStatus status) => status switch
    {
        ErpPurchaseOrderStatus.Draft => "草稿",
        ErpPurchaseOrderStatus.Ordered => "已下单",
        ErpPurchaseOrderStatus.Received => "已到货",
        _ => "已取消",
    };
}

internal sealed class ErpSalesOrderHandlers(
    IAppDbContext db,
    ITenantContext tenant,
    ErpRecords records,
    IStockLedger ledger,
    ITextMatcher text,
    IChangeFeed changes,
    TimeProvider clock) :
    IRequestHandler<ListErpSalesOrdersQuery, PagedResult<ErpSalesOrderDto>>,
    IRequestHandler<GetErpSalesOrderQuery, ErpSalesOrderDto>,
    IRequestHandler<FindErpSalesOrderQuery, ErpSalesOrderDto>,
    IRequestHandler<CreateErpSalesOrderCommand, ErpSalesOrderDto>,
    IRequestHandler<UpdateErpSalesOrderCommand, ErpSalesOrderDto>,
    IRequestHandler<ConfirmErpSalesOrderCommand, ErpSalesOrderDto>,
    IRequestHandler<ShipErpSalesOrderCommand, ErpSalesOrderDto>,
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

    public async Task<ErpSalesOrderDto> HandleAsync(FindErpSalesOrderQuery request, CancellationToken cancellationToken)
    {
        var number = request.Number.Trim().ToUpperInvariant();
        var order = await db.ErpSalesOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Number == number, cancellationToken).ConfigureAwait(false)
                    ?? throw new NotFoundException("erp_sales_order", number);
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
    /// 发货：扣减在库数量并释放对应的预留，每行记一条出库流水。
    /// </summary>
    public async Task<ErpSalesOrderDto> HandleAsync(ShipErpSalesOrderCommand request, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var order = await LockAsync(request.Id, [ErpSalesOrderStatus.Confirmed], "发货", cancellationToken).ConfigureAwait(false);
        var products = await TrackedProductsAsync(order, cancellationToken).ConfigureAwait(false);
        var shortages = new List<string>();
        foreach (var line in TrackedLines(order, products))
        {
            var moved = await records.MoveStockAsync(
                line.ProductId, order.WarehouseId, ErpStockMovementKind.Sale, -line.Quantity, -line.Quantity, false, $"销售订单 {order.Number} 发货", order.Id, null, request.RunId, cancellationToken)
                .ConfigureAwait(false);
            if (moved is null)
            {
                var current = await ledger.LockAsync(line.ProductId, order.WarehouseId, cancellationToken).ConfigureAwait(false);
                shortages.Add(ErpRecords.Shortage(products[line.ProductId], line.Quantity, current.OnHand));
            }
        }

        if (shortages.Count > 0)
        {
            throw new ConflictException("erp.insufficient_stock", $"在库数量不足，无法发货（可能盘点时少了货）：{string.Join("；", shortages)}。请先核实库存。");
        }

        order.Ship(now);
        order.UpdatedAt = now;
        return await PublishAsync(order, ErpEventKeys.SalesOrderShipped, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 取消：草稿直接取消；已确认的订单释放预留的库存。已发货的订单不能取消。
    /// </summary>
    public async Task<ErpSalesOrderDto> HandleAsync(CancelErpSalesOrderCommand request, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var order = await LockAsync(request.Id, [ErpSalesOrderStatus.Draft, ErpSalesOrderStatus.Confirmed], "取消", cancellationToken).ConfigureAwait(false);
        if (order.Status == ErpSalesOrderStatus.Confirmed)
        {
            var products = await TrackedProductsAsync(order, cancellationToken).ConfigureAwait(false);
            foreach (var line in TrackedLines(order, products))
            {
                _ = await ledger.TryChangeAsync(line.ProductId, order.WarehouseId, 0, -line.Quantity, false, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"销售订单 {order.Number} 的预留数量与库存不一致。");
                changes.Publish(ErpRealtime.Stock, line.ProductId, RealtimeChangeKind.Updated);
            }
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

    private static ConflictException StatusChanged(ErpSalesOrderStatus status, string action)
        => new("erp_sales_order.status_changed", $"订单当前是「{ErpLabels.Of(status)}」，不能{action}。已刷新为最新状态。");

    private async Task<Dictionary<Guid, ErpProduct>> TrackedProductsAsync(ErpSalesOrder order, CancellationToken cancellationToken)
    {
        var ids = order.Lines.Select(l => l.ProductId).Distinct().ToList();
        return await db.ErpProducts.AsNoTracking().Where(p => ids.Contains(p.Id) && p.TrackInventory)
            .ToDictionaryAsync(p => p.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 管库存的订单行，按产品 ID 排序：并发订单按同样的顺序锁库存行，不会互相等待成死锁。
    /// </summary>
    private static IEnumerable<ErpOrderLine> TrackedLines(ErpSalesOrder order, Dictionary<Guid, ErpProduct> products)
        => order.Lines.Where(l => products.ContainsKey(l.ProductId)).OrderBy(l => l.ProductId);

    private async Task<ErpSalesOrderDto> PublishAsync(ErpSalesOrder order, string eventType, CancellationToken cancellationToken)
    {
        changes.Publish(ErpRealtime.SalesOrder, order.Id, RealtimeChangeKind.Updated);
        var dto = (await ErpMapping.SalesOrdersAsync(db, [order], cancellationToken).ConfigureAwait(false))[0];
        await records.PublishAsync(eventType, order.Id, () => Task.FromResult(new Dictionary<string, object?> { ["order"] = dto }), cancellationToken).ConfigureAwait(false);
        return dto;
    }

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
        order.OrderDate = r.OrderDate ?? DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        order.Note = r.Note.Trim();
        order.SetLines(lines);
    }
}
