using AiNative.Domain.Common;

namespace AiNative.Domain.Erp;

public enum ErpStockDocumentKind
{
    /// <summary>
    /// 销售发货单。
    /// </summary>
    Shipment = 0,

    /// <summary>
    /// 采购收货单。
    /// </summary>
    Receipt = 1,

    /// <summary>
    /// 销售退货单：客户退回，入库。
    /// </summary>
    SalesReturn = 2,

    /// <summary>
    /// 采购退货单：退给供应商，出库。
    /// </summary>
    PurchaseReturn = 3,
}

/// <summary>
/// 出入库单的一行。金额按订单单价计（订单货币），成本按库存的移动加权平均成本计（产品货币）。
/// </summary>
public sealed class ErpStockDocumentLine
{
    public Guid ProductId { get; set; }

    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal Amount { get; set; }

    public decimal UnitCost { get; set; }

    public decimal CostAmount { get; set; }

    /// <summary>
    /// 成本的货币（当时产品的货币）。与单据货币相同的行才计入毛利。
    /// </summary>
    public string CostCurrency { get; set; } = "CNY";
}

/// <summary>
/// 出入库单：每次发货、收货、退货生成一张，只增不改。订单的已发、已收、已退数量和毛利都由它们汇总而来。
/// </summary>
public sealed class ErpStockDocument : TenantEntity
{
    /// <summary>
    /// 空间内的流水号：发货单 SH-0001、收货单 GR-0001、销售退货 SR-0001、采购退货 PR-0001。
    /// </summary>
    public required string Number { get; set; }

    public ErpStockDocumentKind Kind { get; set; }

    public Guid? SalesOrderId { get; set; }

    public Guid? PurchaseOrderId { get; set; }

    public Guid WarehouseId { get; set; }

    /// <summary>
    /// 订单货币。
    /// </summary>
    public string Currency { get; set; } = "CNY";

    /// <summary>
    /// 本单金额（订单货币）：发货、收货为本次的货款，退货为退款。
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// 备注，退货时是退货原因。
    /// </summary>
    public string Note { get; set; } = string.Empty;

    public List<ErpStockDocumentLine> Lines { get; set; } = [];

    public Actor CreatedBy { get; set; }

    /// <summary>
    /// 由智能体运行产生时记下运行 ID。
    /// </summary>
    public Guid? RunId { get; set; }

    public static string Prefix(ErpStockDocumentKind kind) => kind switch
    {
        ErpStockDocumentKind.Shipment => "SH",
        ErpStockDocumentKind.Receipt => "GR",
        ErpStockDocumentKind.SalesReturn => "SR",
        _ => "PR",
    };

    /// <summary>
    /// 计入销售额、采购额时的方向：发货、收货为正，退货为负。
    /// </summary>
    public static int Sign(ErpStockDocumentKind kind) => kind is ErpStockDocumentKind.Shipment or ErpStockDocumentKind.Receipt ? 1 : -1;
}
