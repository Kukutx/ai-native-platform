using AiNative.Domain.Common;

namespace AiNative.Domain.Erp;

/// <summary>
/// 产品（商品、物料或服务）。SKU 在空间内唯一；不管库存的产品（例如服务）下单时不占用库存。
/// </summary>
public sealed class ErpProduct : TenantEntity
{
    public required string Sku { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// 计量单位，例如件、箱、千克、小时。
    /// </summary>
    public string Unit { get; set; } = "件";

    public string Category { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 标准售价，销售订单不填单价时使用。
    /// </summary>
    public decimal? Price { get; set; }

    /// <summary>
    /// 参考采购价，采购订单不填单价时使用。
    /// </summary>
    public decimal? Cost { get; set; }

    /// <summary>
    /// 售价与采购价的货币（ISO 4217）。
    /// </summary>
    public string Currency { get; set; } = "CNY";

    public bool TrackInventory { get; set; } = true;

    /// <summary>
    /// 补货提醒线：全部仓库的可用库存合计降到它以下时发出低库存事件。
    /// </summary>
    public decimal? ReorderPoint { get; set; }

    /// <summary>
    /// 停用的产品不能再下单，历史订单和库存记录保留。
    /// </summary>
    public bool IsActive { get; set; } = true;

    public Actor CreatedBy { get; set; }
}

/// <summary>
/// 仓库。每个空间有且只有一个默认仓库，订单没指定仓库时使用它。
/// </summary>
public sealed class ErpWarehouse : TenantEntity
{
    public required string Name { get; set; }

    public string Address { get; set; } = string.Empty;

    public bool IsDefault { get; set; }
}

/// <summary>
/// 某个产品在某个仓库的库存。在库数量只能由库存流水改变；预留是已确认未发货的销售订单占用的数量。
/// </summary>
public sealed class ErpStockLevel : TenantEntity
{
    public Guid ProductId { get; set; }

    public Guid WarehouseId { get; set; }

    public decimal OnHand { get; set; }

    public decimal Reserved { get; set; }

    public decimal Available => OnHand - Reserved;
}

public enum ErpStockMovementKind
{
    /// <summary>
    /// 采购到货入库。
    /// </summary>
    Purchase = 0,

    /// <summary>
    /// 销售发货出库。
    /// </summary>
    Sale = 1,

    /// <summary>
    /// 手工调整（盘点、报损、期初录入）。
    /// </summary>
    Adjustment = 2,

    TransferIn = 3,

    TransferOut = 4,
}

/// <summary>
/// 库存流水：在库数量的每一次变化都留一条，只增不改。
/// </summary>
public sealed class ErpStockMovement : TenantEntity
{
    public Guid ProductId { get; set; }

    public Guid WarehouseId { get; set; }

    public ErpStockMovementKind Kind { get; set; }

    /// <summary>
    /// 变化量：入库为正，出库为负。
    /// </summary>
    public decimal Quantity { get; set; }

    /// <summary>
    /// 变化后该仓库的在库数量。
    /// </summary>
    public decimal BalanceAfter { get; set; }

    public string Note { get; set; } = string.Empty;

    public Guid? SalesOrderId { get; set; }

    public Guid? PurchaseOrderId { get; set; }

    public Actor Actor { get; set; }

    /// <summary>
    /// 由智能体运行产生时记下运行 ID，便于追溯。
    /// </summary>
    public Guid? RunId { get; set; }
}
