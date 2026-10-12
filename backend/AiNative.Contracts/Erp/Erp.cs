using AiNative.Contracts.Common;
using AiNative.Domain.Erp;

namespace AiNative.Contracts.Erp;

/// <summary>
/// 产品。OnHand、Reserved、Available、StockValue 为全部仓库的合计；不管库存的产品都是 0。
/// AverageCost 为库存金额 ÷ 在库数量（产品货币），没有库存时为空。
/// </summary>
public sealed record ErpProductDto(
    Guid Id,
    string Sku,
    string Name,
    string Unit,
    string Category,
    string Description,
    decimal? Price,
    decimal? Cost,
    string Currency,
    bool TrackInventory,
    decimal? ReorderPoint,
    bool IsActive,
    decimal OnHand,
    decimal Reserved,
    decimal Available,
    bool LowStock,
    decimal? AverageCost,
    decimal StockValue,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ErpWarehouseDto(Guid Id, string Name, string Address, bool IsDefault, DateTimeOffset CreatedAt);

/// <param name="Currency">平均成本和库存金额的货币，即产品的货币。</param>
public sealed record ErpStockLevelDto(
    Guid ProductId,
    string Sku,
    string ProductName,
    string Unit,
    Guid WarehouseId,
    string WarehouseName,
    decimal OnHand,
    decimal Reserved,
    decimal Available,
    decimal AverageCost,
    decimal StockValue,
    string Currency,
    DateTimeOffset UpdatedAt);

/// <param name="Currency">成本的货币，即产品的货币。</param>
public sealed record ErpStockMovementDto(
    Guid Id,
    Guid ProductId,
    string Sku,
    string ProductName,
    string Unit,
    Guid WarehouseId,
    string WarehouseName,
    ErpStockMovementKind Kind,
    decimal Quantity,
    decimal BalanceAfter,
    decimal UnitCost,
    decimal CostAmount,
    string Currency,
    string Note,
    Guid? SalesOrderId,
    string? SalesOrderNumber,
    Guid? PurchaseOrderId,
    string? PurchaseOrderNumber,
    Guid? DocumentId,
    string? DocumentNumber,
    ActorDto Actor,
    DateTimeOffset CreatedAt);

public sealed record ErpProductDetailDto(
    ErpProductDto Product,
    IReadOnlyList<ErpStockLevelDto> Levels,
    IReadOnlyList<ErpStockMovementDto> Movements);

/// <summary>
/// 订单行。Delivered 为已发货（销售）或已收货（采购）的数量，Returned 为已退货的数量。
/// </summary>
public sealed record ErpOrderLineDto(
    Guid ProductId,
    string Sku,
    string ProductName,
    string Unit,
    decimal Quantity,
    decimal UnitPrice,
    decimal Amount,
    decimal Delivered,
    decimal Returned);

/// <summary>
/// 销售订单。ShippedAmount 为已发货金额减去退款；CostOfGoods 为对应的销售成本，GrossProfit = ShippedAmount − CostOfGoods。
/// 有明细的成本货币与订单货币不同时（暂不做汇率换算）这两项为空。
/// </summary>
public sealed record ErpSalesOrderDto(
    Guid Id,
    string Number,
    ErpSalesOrderStatus Status,
    Guid? CompanyId,
    string? CompanyName,
    Guid? ContactId,
    string? ContactName,
    Guid? DealId,
    string? DealTitle,
    Guid WarehouseId,
    string WarehouseName,
    string Currency,
    DateOnly OrderDate,
    string Note,
    decimal Total,
    IReadOnlyList<ErpOrderLineDto> Lines,
    decimal ShippedAmount,
    decimal? CostOfGoods,
    decimal? GrossProfit,
    ActorDto? Owner,
    ActorDto CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? ShippedAt,
    DateTimeOffset? CancelledAt);

/// <summary>
/// 采购订单。ReceivedAmount 为已到货金额减去退给供应商的金额。
/// </summary>
public sealed record ErpPurchaseOrderDto(
    Guid Id,
    string Number,
    ErpPurchaseOrderStatus Status,
    Guid? SupplierId,
    string? SupplierName,
    Guid WarehouseId,
    string WarehouseName,
    string Currency,
    DateOnly OrderDate,
    DateOnly? ExpectedDate,
    string Note,
    decimal Total,
    IReadOnlyList<ErpOrderLineDto> Lines,
    decimal ReceivedAmount,
    ActorDto CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? OrderedAt,
    DateTimeOffset? ReceivedAt,
    DateTimeOffset? CancelledAt);

public sealed record ErpStockDocumentLineDto(
    Guid ProductId,
    string Sku,
    string ProductName,
    string Unit,
    decimal Quantity,
    decimal UnitPrice,
    decimal Amount,
    decimal UnitCost,
    decimal CostAmount,
    string CostCurrency);

/// <summary>
/// 出入库单（发货单、收货单、退货单）。CompanyId / CompanyName 为销售订单的客户或采购订单的供应商；
/// Amount 为订单货币的货款或退款。
/// </summary>
public sealed record ErpStockDocumentDto(
    Guid Id,
    string Number,
    ErpStockDocumentKind Kind,
    Guid? SalesOrderId,
    Guid? PurchaseOrderId,
    string? OrderNumber,
    Guid? CompanyId,
    string? CompanyName,
    Guid WarehouseId,
    string WarehouseName,
    string Currency,
    decimal Amount,
    string Note,
    IReadOnlyList<ErpStockDocumentLineDto> Lines,
    ActorDto CreatedBy,
    DateTimeOffset CreatedAt);

/// <summary>
/// 同一货币的金额合计。不同货币不做换算，分别列出。
/// </summary>
public sealed record ErpAmountDto(string Currency, decimal Amount);

/// <summary>
/// 进销存概况：本月发货（扣除退货）、毛利与到货金额，待发货（含部分发货，金额为未发部分）与待到货的订单，
/// 库存金额（按产品货币）和低库存产品。
/// </summary>
public sealed record ErpSummaryDto(
    IReadOnlyList<ErpAmountDto> ShippedThisMonth,
    IReadOnlyList<ErpAmountDto> GrossProfitThisMonth,
    IReadOnlyList<ErpAmountDto> ReceivedThisMonth,
    int DraftSalesOrders,
    int ToShipSalesOrders,
    IReadOnlyList<ErpAmountDto> ToShipTotals,
    int OpenPurchaseOrders,
    int ActiveProducts,
    int LowStockProducts,
    IReadOnlyList<ErpAmountDto> StockValue);

public sealed record SaveErpProductRequest
{
    public required string Sku { get; init; }

    public required string Name { get; init; }

    public string Unit { get; init; } = "件";

    public string Category { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public decimal? Price { get; init; }

    public decimal? Cost { get; init; }

    public string Currency { get; init; } = "CNY";

    public bool TrackInventory { get; init; } = true;

    public decimal? ReorderPoint { get; init; }

    public bool IsActive { get; init; } = true;

    /// <summary>
    /// 编辑时回传读取到的 UpdatedAt；期间被别人改过则返回 409。
    /// </summary>
    public DateTimeOffset? ExpectedUpdatedAt { get; init; }
}

public sealed record SaveErpWarehouseRequest
{
    public required string Name { get; init; }

    public string Address { get; init; } = string.Empty;

    /// <summary>
    /// 设为默认仓库（原来的默认仓库自动取消）。
    /// </summary>
    public bool IsDefault { get; init; }
}

/// <summary>
/// 手工调整库存：Quantity 为变化量（入库为正、出库为负），CountedQuantity 为盘点后的实际数量，二选一。
/// </summary>
public sealed record AdjustErpStockRequest
{
    public required Guid ProductId { get; init; }

    /// <summary>
    /// 仓库；留空为默认仓库。
    /// </summary>
    public Guid? WarehouseId { get; init; }

    public decimal? Quantity { get; init; }

    public decimal? CountedQuantity { get; init; }

    /// <summary>
    /// 增加库存时的单位成本（产品货币），例如期初录入时的进价；留空按该仓库当前的平均成本入库。减少库存时忽略。
    /// </summary>
    public decimal? UnitCost { get; init; }

    /// <summary>
    /// 原因，例如期初录入、盘点、报损。
    /// </summary>
    public string Note { get; init; } = string.Empty;
}

public sealed record TransferErpStockRequest
{
    public required Guid ProductId { get; init; }

    public required Guid FromWarehouseId { get; init; }

    public required Guid ToWarehouseId { get; init; }

    public required decimal Quantity { get; init; }

    public string Note { get; init; } = string.Empty;
}

public sealed record ErpOrderLineRequest
{
    public required Guid ProductId { get; init; }

    public required decimal Quantity { get; init; }

    /// <summary>
    /// 单价；留空时销售订单用产品售价、采购订单用参考采购价（货币须与订单一致）。
    /// </summary>
    public decimal? UnitPrice { get; init; }
}

public sealed record SaveErpSalesOrderRequest
{
    public Guid? CompanyId { get; init; }

    public Guid? ContactId { get; init; }

    /// <summary>
    /// 来源商机；没填客户、联系人、货币时从商机带出。
    /// </summary>
    public Guid? DealId { get; init; }

    /// <summary>
    /// 发货仓库；留空为默认仓库。
    /// </summary>
    public Guid? WarehouseId { get; init; }

    /// <summary>
    /// 货币；留空时跟随商机，没有商机为 CNY。
    /// </summary>
    public string? Currency { get; init; }

    /// <summary>
    /// 下单日期；留空为今天。
    /// </summary>
    public DateOnly? OrderDate { get; init; }

    public string Note { get; init; } = string.Empty;

    public Guid? OwnerUserId { get; init; }

    public required IReadOnlyList<ErpOrderLineRequest> Lines { get; init; }

    public DateTimeOffset? ExpectedUpdatedAt { get; init; }
}

public sealed record SaveErpPurchaseOrderRequest
{
    public required Guid SupplierId { get; init; }

    /// <summary>
    /// 收货仓库；留空为默认仓库。
    /// </summary>
    public Guid? WarehouseId { get; init; }

    public string Currency { get; init; } = "CNY";

    public DateOnly? OrderDate { get; init; }

    public DateOnly? ExpectedDate { get; init; }

    public string Note { get; init; } = string.Empty;

    public required IReadOnlyList<ErpOrderLineRequest> Lines { get; init; }

    public DateTimeOffset? ExpectedUpdatedAt { get; init; }
}

/// <summary>
/// 发货、收货或退货的一行：哪个产品、多少数量。
/// </summary>
public sealed record ErpQuantityLineRequest
{
    public required Guid ProductId { get; init; }

    public required decimal Quantity { get; init; }
}

/// <summary>
/// 一次发货（销售订单）或收货（采购订单）。
/// </summary>
public sealed record ErpDeliveryRequest
{
    /// <summary>
    /// 本次的产品和数量；留空为全部还没发（收）的数量。
    /// </summary>
    public IReadOnlyList<ErpQuantityLineRequest>? Lines { get; init; }

    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// 一次退货：销售订单为客户退回入库，采购订单为退给供应商出库。
/// </summary>
public sealed record ErpReturnRequest
{
    public required IReadOnlyList<ErpQuantityLineRequest> Lines { get; init; }

    /// <summary>
    /// 退回入库（或从哪里退出）的仓库；留空为订单的仓库。
    /// </summary>
    public Guid? WarehouseId { get; init; }

    /// <summary>
    /// 退货原因。
    /// </summary>
    public string Note { get; init; } = string.Empty;
}
