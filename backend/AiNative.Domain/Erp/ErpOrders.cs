using AiNative.Domain.Common;

namespace AiNative.Domain.Erp;

/// <summary>
/// 订单行。金额 = 数量 × 单价，四舍五入到分。同一产品在一张订单里只有一行，发货、收货、退货都按产品对应到行。
/// </summary>
public sealed class ErpOrderLine
{
    public Guid ProductId { get; set; }

    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal Amount { get; set; }

    /// <summary>
    /// 已发货（销售订单）或已收货（采购订单）的数量，可以分多次。
    /// </summary>
    public decimal Delivered { get; set; }

    /// <summary>
    /// 已退货的数量，不超过已发货（已收货）的数量。
    /// </summary>
    public decimal Returned { get; set; }

    /// <summary>
    /// 还没发货（收货）的数量。
    /// </summary>
    public decimal Remaining => Quantity - Delivered;

    /// <summary>
    /// 还可以退的数量。
    /// </summary>
    public decimal Returnable => Delivered - Returned;

    /// <summary>
    /// 把累计数量从 <paramref name="from"/> 增加到 <paramref name="to"/> 对应的金额：按累计数量算出金额再相减，
    /// 分几次发完时各次金额加起来正好等于整行金额，不会差一分钱。
    /// </summary>
    public decimal AmountBetween(decimal from, decimal to) => Round(to * UnitPrice) - Round(from * UnitPrice);

    public static ErpOrderLine Create(Guid productId, decimal quantity, decimal unitPrice)
    {
        if (quantity <= 0)
        {
            throw new DomainException("erp_order.invalid_quantity", "数量必须大于 0。");
        }

        if (unitPrice < 0)
        {
            throw new DomainException("erp_order.invalid_price", "单价不能为负数。");
        }

        return new ErpOrderLine
        {
            ProductId = productId,
            Quantity = quantity,
            UnitPrice = unitPrice,
            Amount = Round(quantity * unitPrice),
        };
    }

    private static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}

/// <summary>
/// 订单行上的发货（收货）与退货登记，销售订单和采购订单共用。
/// </summary>
internal static class ErpOrderLines
{
    /// <summary>
    /// 按产品登记本次发货（收货）的数量，返回是否已全部发完（收完）。
    /// </summary>
    public static bool Deliver(List<ErpOrderLine> lines, IReadOnlyDictionary<Guid, decimal> quantities, string verb)
    {
        foreach (var (productId, quantity) in quantities)
        {
            var line = Find(lines, productId);
            if (quantity <= 0 || quantity > line.Remaining)
            {
                throw new DomainException("erp_order.invalid_quantity", $"本次{verb}数量必须大于 0，且不超过未{verb}的数量。");
            }
        }

        foreach (var (productId, quantity) in quantities)
        {
            Find(lines, productId).Delivered += quantity;
        }

        return lines.All(l => l.Remaining == 0);
    }

    public static void Return(List<ErpOrderLine> lines, IReadOnlyDictionary<Guid, decimal> quantities, string verb)
    {
        foreach (var (productId, quantity) in quantities)
        {
            var line = Find(lines, productId);
            if (quantity <= 0 || quantity > line.Returnable)
            {
                throw new DomainException("erp_order.invalid_quantity", $"退货数量必须大于 0，且不超过已{verb}未退的数量。");
            }
        }

        foreach (var (productId, quantity) in quantities)
        {
            Find(lines, productId).Returned += quantity;
        }
    }

    private static ErpOrderLine Find(List<ErpOrderLine> lines, Guid productId)
        => lines.Find(l => l.ProductId == productId) ?? throw new DomainException("erp_order.unknown_product", "订单里没有这个产品。");
}

public enum ErpSalesOrderStatus
{
    /// <summary>
    /// 草稿：可以修改，不占库存。
    /// </summary>
    Draft = 0,

    /// <summary>
    /// 已确认：按订单行预留库存，等待发货。
    /// </summary>
    Confirmed = 1,

    /// <summary>
    /// 部分发货：已经发出一部分，剩下的仍然预留着。
    /// </summary>
    PartiallyShipped = 4,

    /// <summary>
    /// 已发货：全部发完，或者结束发货（不再发剩下的）。
    /// </summary>
    Shipped = 2,

    Cancelled = 3,
}

/// <summary>
/// 销售订单：草稿 → 确认（预留库存）→ 发货（扣减库存，可以分多次）；一件没发之前可以取消，取消时释放预留；
/// 部分发货后可以结束发货，释放剩下的预留。发出的货可以退回。
/// </summary>
public sealed class ErpSalesOrder : TenantEntity
{
    /// <summary>
    /// 空间内的流水号，例如 SO-0001。
    /// </summary>
    public required string Number { get; set; }

    /// <summary>
    /// 客户（客户管理里的客户）。
    /// </summary>
    public Guid? CompanyId { get; set; }

    public Guid? ContactId { get; set; }

    /// <summary>
    /// 来源商机。
    /// </summary>
    public Guid? DealId { get; set; }

    /// <summary>
    /// 发货仓库。
    /// </summary>
    public Guid WarehouseId { get; set; }

    public ErpSalesOrderStatus Status { get; private set; }

    public string Currency { get; set; } = "CNY";

    public DateOnly OrderDate { get; set; }

    public string Note { get; set; } = string.Empty;

    public decimal Total { get; private set; }

    public List<ErpOrderLine> Lines { get; private set; } = [];

    public Guid? OwnerUserId { get; set; }

    public Actor CreatedBy { get; set; }

    public DateTimeOffset? ConfirmedAt { get; private set; }

    /// <summary>
    /// 全部发完或结束发货的时间。
    /// </summary>
    public DateTimeOffset? ShippedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public void SetLines(IEnumerable<ErpOrderLine> lines)
    {
        EnsureStatus(ErpSalesOrderStatus.Draft, "只有草稿订单可以修改。");
        Lines = [.. lines];
        if (Lines.Count == 0)
        {
            throw new DomainException("erp_order.no_lines", "订单至少需要一行产品。");
        }

        Total = Lines.Sum(l => l.Amount);
    }

    public void Confirm(DateTimeOffset now)
    {
        EnsureStatus(ErpSalesOrderStatus.Draft, "只有草稿订单可以确认。");
        Status = ErpSalesOrderStatus.Confirmed;
        ConfirmedAt = now;
    }

    /// <summary>
    /// 登记一次发货：全部发完时变为已发货，否则为部分发货。
    /// </summary>
    public void Ship(IReadOnlyDictionary<Guid, decimal> quantities, DateTimeOffset now)
    {
        if (Status is not (ErpSalesOrderStatus.Confirmed or ErpSalesOrderStatus.PartiallyShipped))
        {
            throw new DomainException("erp_sales_order.invalid_status", "只有已确认或部分发货的订单可以发货。");
        }

        if (ErpOrderLines.Deliver(Lines, quantities, "发货"))
        {
            Status = ErpSalesOrderStatus.Shipped;
            ShippedAt = now;
        }
        else
        {
            Status = ErpSalesOrderStatus.PartiallyShipped;
        }
    }

    /// <summary>
    /// 结束发货：部分发货的订单不再发剩下的数量。
    /// </summary>
    public void CloseShipping(DateTimeOffset now)
    {
        EnsureStatus(ErpSalesOrderStatus.PartiallyShipped, "只有部分发货的订单可以结束发货。");
        Status = ErpSalesOrderStatus.Shipped;
        ShippedAt = now;
    }

    /// <summary>
    /// 登记客户退货：只能退已经发出的货。
    /// </summary>
    public void Return(IReadOnlyDictionary<Guid, decimal> quantities)
    {
        if (Status is not (ErpSalesOrderStatus.PartiallyShipped or ErpSalesOrderStatus.Shipped))
        {
            throw new DomainException("erp_sales_order.invalid_status", "订单还没有发货，不能退货。");
        }

        ErpOrderLines.Return(Lines, quantities, "发货");
    }

    public void Cancel(DateTimeOffset now)
    {
        if (Status is not (ErpSalesOrderStatus.Draft or ErpSalesOrderStatus.Confirmed))
        {
            throw new DomainException("erp_sales_order.invalid_status", "已经发货或已取消的订单不能取消。");
        }

        Status = ErpSalesOrderStatus.Cancelled;
        CancelledAt = now;
    }

    private void EnsureStatus(ErpSalesOrderStatus expected, string message)
    {
        if (Status != expected)
        {
            throw new DomainException("erp_sales_order.invalid_status", message);
        }
    }
}

public enum ErpPurchaseOrderStatus
{
    /// <summary>
    /// 草稿：可以修改。
    /// </summary>
    Draft = 0,

    /// <summary>
    /// 已下单：已发给供应商，等待到货。
    /// </summary>
    Ordered = 1,

    /// <summary>
    /// 部分到货：已经收到一部分。
    /// </summary>
    PartiallyReceived = 4,

    /// <summary>
    /// 已到货：全部收完，或者结束收货（剩下的不再等）。
    /// </summary>
    Received = 2,

    Cancelled = 3,
}

/// <summary>
/// 采购订单：草稿 → 下单 → 到货入库（可以分多次）；一件没到之前可以取消，部分到货后可以结束收货。收到的货可以退给供应商。
/// </summary>
public sealed class ErpPurchaseOrder : TenantEntity
{
    /// <summary>
    /// 空间内的流水号，例如 PO-0001。
    /// </summary>
    public required string Number { get; set; }

    /// <summary>
    /// 供应商（客户管理里的客户，同一家公司既可以是客户也可以是供应商）。
    /// </summary>
    public Guid? SupplierId { get; set; }

    /// <summary>
    /// 收货仓库。
    /// </summary>
    public Guid WarehouseId { get; set; }

    public ErpPurchaseOrderStatus Status { get; private set; }

    public string Currency { get; set; } = "CNY";

    public DateOnly OrderDate { get; set; }

    /// <summary>
    /// 预计到货日期。
    /// </summary>
    public DateOnly? ExpectedDate { get; set; }

    public string Note { get; set; } = string.Empty;

    public decimal Total { get; private set; }

    public List<ErpOrderLine> Lines { get; private set; } = [];

    public Actor CreatedBy { get; set; }

    public DateTimeOffset? OrderedAt { get; private set; }

    /// <summary>
    /// 全部收完或结束收货的时间。
    /// </summary>
    public DateTimeOffset? ReceivedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public void SetLines(IEnumerable<ErpOrderLine> lines)
    {
        EnsureStatus(ErpPurchaseOrderStatus.Draft, "只有草稿订单可以修改。");
        Lines = [.. lines];
        if (Lines.Count == 0)
        {
            throw new DomainException("erp_order.no_lines", "订单至少需要一行产品。");
        }

        Total = Lines.Sum(l => l.Amount);
    }

    public void Place(DateTimeOffset now)
    {
        EnsureStatus(ErpPurchaseOrderStatus.Draft, "只有草稿订单可以下单。");
        Status = ErpPurchaseOrderStatus.Ordered;
        OrderedAt = now;
    }

    /// <summary>
    /// 登记一次到货：全部收完时变为已到货，否则为部分到货。
    /// </summary>
    public void Receive(IReadOnlyDictionary<Guid, decimal> quantities, DateTimeOffset now)
    {
        if (Status is not (ErpPurchaseOrderStatus.Ordered or ErpPurchaseOrderStatus.PartiallyReceived))
        {
            throw new DomainException("erp_purchase_order.invalid_status", "只有已下单或部分到货的订单可以收货。");
        }

        if (ErpOrderLines.Deliver(Lines, quantities, "收货"))
        {
            Status = ErpPurchaseOrderStatus.Received;
            ReceivedAt = now;
        }
        else
        {
            Status = ErpPurchaseOrderStatus.PartiallyReceived;
        }
    }

    /// <summary>
    /// 结束收货：部分到货的订单剩下的不再等。
    /// </summary>
    public void CloseReceiving(DateTimeOffset now)
    {
        EnsureStatus(ErpPurchaseOrderStatus.PartiallyReceived, "只有部分到货的订单可以结束收货。");
        Status = ErpPurchaseOrderStatus.Received;
        ReceivedAt = now;
    }

    /// <summary>
    /// 登记退货给供应商：只能退已经收到的货。
    /// </summary>
    public void Return(IReadOnlyDictionary<Guid, decimal> quantities)
    {
        if (Status is not (ErpPurchaseOrderStatus.PartiallyReceived or ErpPurchaseOrderStatus.Received))
        {
            throw new DomainException("erp_purchase_order.invalid_status", "订单还没有到货，不能退货。");
        }

        ErpOrderLines.Return(Lines, quantities, "收货");
    }

    public void Cancel(DateTimeOffset now)
    {
        if (Status is not (ErpPurchaseOrderStatus.Draft or ErpPurchaseOrderStatus.Ordered))
        {
            throw new DomainException("erp_purchase_order.invalid_status", "已经到货或已取消的订单不能取消。");
        }

        Status = ErpPurchaseOrderStatus.Cancelled;
        CancelledAt = now;
    }

    private void EnsureStatus(ErpPurchaseOrderStatus expected, string message)
    {
        if (Status != expected)
        {
            throw new DomainException("erp_purchase_order.invalid_status", message);
        }
    }
}
