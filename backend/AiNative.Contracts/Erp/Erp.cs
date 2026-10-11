using AiNative.Contracts.Common;
using AiNative.Domain.Erp;

namespace AiNative.Contracts.Erp;

/// <summary>
/// 产品。OnHand、Reserved、Available 为全部仓库的合计；不管库存的产品都是 0。
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
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ErpWarehouseDto(Guid Id, string Name, string Address, bool IsDefault, DateTimeOffset CreatedAt);

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
    DateTimeOffset UpdatedAt);

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
    string Note,
    Guid? SalesOrderId,
    string? SalesOrderNumber,
    Guid? PurchaseOrderId,
    string? PurchaseOrderNumber,
    ActorDto Actor,
    DateTimeOffset CreatedAt);

public sealed record ErpProductDetailDto(
    ErpProductDto Product,
    IReadOnlyList<ErpStockLevelDto> Levels,
    IReadOnlyList<ErpStockMovementDto> Movements);

public sealed record ErpOrderLineDto(
    Guid ProductId,
    string Sku,
    string ProductName,
    string Unit,
    decimal Quantity,
    decimal UnitPrice,
    decimal Amount);

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
    ActorDto? Owner,
    ActorDto CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? ShippedAt,
    DateTimeOffset? CancelledAt);

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
    ActorDto CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? OrderedAt,
    DateTimeOffset? ReceivedAt,
    DateTimeOffset? CancelledAt);

/// <summary>
/// 同一货币的金额合计。不同货币不做换算，分别列出。
/// </summary>
public sealed record ErpAmountDto(string Currency, decimal Amount);

/// <summary>
/// 进销存概况：本月发货金额、待发货与待到货的订单、低库存产品。
/// </summary>
public sealed record ErpSummaryDto(
    IReadOnlyList<ErpAmountDto> ShippedThisMonth,
    IReadOnlyList<ErpAmountDto> ReceivedThisMonth,
    int DraftSalesOrders,
    int ToShipSalesOrders,
    IReadOnlyList<ErpAmountDto> ToShipTotals,
    int OpenPurchaseOrders,
    int ActiveProducts,
    int LowStockProducts);

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
