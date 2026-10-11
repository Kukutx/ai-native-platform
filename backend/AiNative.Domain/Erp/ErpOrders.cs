using AiNative.Domain.Common;

namespace AiNative.Domain.Erp;

/// <summary>
/// 订单行。金额 = 数量 × 单价，四舍五入到分。
/// </summary>
public sealed class ErpOrderLine
{
    public Guid ProductId { get; set; }

    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal Amount { get; set; }

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
            Amount = Math.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero),
        };
    }
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
    /// 已发货：库存已扣减。
    /// </summary>
    Shipped = 2,

    Cancelled = 3,
}

/// <summary>
/// 销售订单：草稿 → 确认（预留库存）→ 发货（扣减库存）；发货前可以取消，取消时释放预留。
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

    public void Ship(DateTimeOffset now)
    {
        EnsureStatus(ErpSalesOrderStatus.Confirmed, "只有已确认的订单可以发货。");
        Status = ErpSalesOrderStatus.Shipped;
        ShippedAt = now;
    }

    public void Cancel(DateTimeOffset now)
    {
        if (Status is not (ErpSalesOrderStatus.Draft or ErpSalesOrderStatus.Confirmed))
        {
            throw new DomainException("erp_sales_order.invalid_status", "已发货或已取消的订单不能取消。");
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
    /// 已到货：全部入库。
    /// </summary>
    Received = 2,

    Cancelled = 3,
}

/// <summary>
/// 采购订单：草稿 → 下单 → 到货（全部入库）；到货前可以取消。
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

    public void Receive(DateTimeOffset now)
    {
        EnsureStatus(ErpPurchaseOrderStatus.Ordered, "只有已下单的订单可以收货。");
        Status = ErpPurchaseOrderStatus.Received;
        ReceivedAt = now;
    }

    public void Cancel(DateTimeOffset now)
    {
        if (Status is not (ErpPurchaseOrderStatus.Draft or ErpPurchaseOrderStatus.Ordered))
        {
            throw new DomainException("erp_purchase_order.invalid_status", "已到货或已取消的订单不能取消。");
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
