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
public sealed record ListErpPurchaseOrdersQuery(
    string? Search = null,
    ErpPurchaseOrderStatus? Status = null,
    Guid? SupplierId = null,
    int Page = 1,
    int PageSize = 50) : IQuery<PagedResult<ErpPurchaseOrderDto>>;

[RequireFeature(ErpFeatures.View)]
public sealed record GetErpPurchaseOrderQuery(Guid Id) : IQuery<ErpPurchaseOrderDto>;

/// <summary>
/// 按订单号（例如 PO-0001，不区分大小写）查找。
/// </summary>
[RequireFeature(ErpFeatures.View)]
public sealed record FindErpPurchaseOrderQuery(string Number) : IQuery<ErpPurchaseOrderDto>;

[RequireFeature(ErpFeatures.Purchase)]
public sealed record CreateErpPurchaseOrderCommand(SaveErpPurchaseOrderRequest Request) : ICommand<ErpPurchaseOrderDto>;

[RequireFeature(ErpFeatures.Purchase)]
public sealed record UpdateErpPurchaseOrderCommand(Guid Id, SaveErpPurchaseOrderRequest Request) : ICommand<ErpPurchaseOrderDto>;

[RequireFeature(ErpFeatures.Purchase)]
public sealed record PlaceErpPurchaseOrderCommand(Guid Id) : ICommand<ErpPurchaseOrderDto>;

[RequireFeature(ErpFeatures.Purchase)]
public sealed record ReceiveErpPurchaseOrderCommand(Guid Id, Guid? RunId = null) : ICommand<ErpPurchaseOrderDto>;

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
        RuleFor(x => x.ExpectedDate).GreaterThanOrEqualTo(x => x.OrderDate!.Value).When(x => x.ExpectedDate.HasValue && x.OrderDate.HasValue)
            .WithMessage("预计到货日期不能早于下单日期。").OverridePropertyName("expectedDate");
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

internal sealed class ErpPurchaseOrderHandlers(
    IAppDbContext db,
    ITenantContext tenant,
    ErpRecords records,
    ITextMatcher text,
    IChangeFeed changes,
    TimeProvider clock) :
    IRequestHandler<ListErpPurchaseOrdersQuery, PagedResult<ErpPurchaseOrderDto>>,
    IRequestHandler<GetErpPurchaseOrderQuery, ErpPurchaseOrderDto>,
    IRequestHandler<FindErpPurchaseOrderQuery, ErpPurchaseOrderDto>,
    IRequestHandler<CreateErpPurchaseOrderCommand, ErpPurchaseOrderDto>,
    IRequestHandler<UpdateErpPurchaseOrderCommand, ErpPurchaseOrderDto>,
    IRequestHandler<PlaceErpPurchaseOrderCommand, ErpPurchaseOrderDto>,
    IRequestHandler<ReceiveErpPurchaseOrderCommand, ErpPurchaseOrderDto>,
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

    public async Task<ErpPurchaseOrderDto> HandleAsync(FindErpPurchaseOrderQuery request, CancellationToken cancellationToken)
    {
        var number = request.Number.Trim().ToUpperInvariant();
        var order = await db.ErpPurchaseOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Number == number, cancellationToken).ConfigureAwait(false)
                    ?? throw new NotFoundException("erp_purchase_order", number);
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
    /// 到货：管库存的订单行全部入库，每行记一条入库流水。
    /// </summary>
    public async Task<ErpPurchaseOrderDto> HandleAsync(ReceiveErpPurchaseOrderCommand request, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var order = await LockAsync(request.Id, [ErpPurchaseOrderStatus.Ordered], "收货", cancellationToken).ConfigureAwait(false);
        var ids = order.Lines.Select(l => l.ProductId).Distinct().ToList();
        var tracked = await db.ErpProducts.AsNoTracking().Where(p => ids.Contains(p.Id) && p.TrackInventory).Select(p => p.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var line in order.Lines.Where(l => tracked.Contains(l.ProductId)).OrderBy(l => l.ProductId))
        {
            _ = await records.MoveStockAsync(
                    line.ProductId, order.WarehouseId, ErpStockMovementKind.Purchase, line.Quantity, 0, false, $"采购订单 {order.Number} 到货", null, order.Id, request.RunId, cancellationToken)
                    .ConfigureAwait(false)
                ?? throw new InvalidOperationException($"采购订单 {order.Number} 入库失败。");
        }

        order.Receive(now);
        order.UpdatedAt = now;
        changes.Publish(ErpRealtime.PurchaseOrder, order.Id, RealtimeChangeKind.Updated);
        var dto = (await ErpMapping.PurchaseOrdersAsync(db, [order], cancellationToken).ConfigureAwait(false))[0];
        await records.PublishAsync(ErpEventKeys.PurchaseOrderReceived, order.Id, () => Task.FromResult(new Dictionary<string, object?> { ["order"] = dto }), cancellationToken)
            .ConfigureAwait(false);
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
        if (!await db.CrmCompanies.AnyAsync(c => c.Id == r.SupplierId, cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException("crm_company", r.SupplierId);
        }

        var warehouse = await records.ResolveWarehouseAsync(r.WarehouseId, cancellationToken).ConfigureAwait(false);
        order.Currency = r.Currency.Trim().ToUpperInvariant();
        var (lines, _) = await records.BuildLinesAsync(r.Lines, order.Currency, p => p.Cost, cancellationToken).ConfigureAwait(false);
        order.SupplierId = r.SupplierId;
        order.WarehouseId = warehouse.Id;
        order.OrderDate = r.OrderDate ?? DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        order.ExpectedDate = r.ExpectedDate;
        order.Note = r.Note.Trim();
        order.SetLines(lines);
    }
}
