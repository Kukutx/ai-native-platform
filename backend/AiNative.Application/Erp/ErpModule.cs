using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Crm;
using AiNative.Application.Modules;
using AiNative.Domain.Erp;

namespace AiNative.Application.Erp;

public static class ErpFeatures
{
    public const string View = "erp.view";
    public const string Sales = "erp.sales";
    public const string Purchase = "erp.purchase";
    public const string Inventory = "erp.inventory";
}

public static class ErpEventKeys
{
    public const string SalesOrderConfirmed = "erp.sales_order_confirmed";
    public const string SalesOrderShipped = "erp.sales_order_shipped";
    public const string PurchaseOrderReceived = "erp.purchase_order_received";
    public const string SalesOrderReturned = "erp.sales_order_returned";
    public const string PurchaseOrderReturned = "erp.purchase_order_returned";
    public const string StockLow = "erp.stock_low";
}

/// <summary>
/// 进销存：产品、仓库库存、销售订单与采购订单。客户和供应商直接使用客户管理里的客户，所以依赖客户管理模块。
/// </summary>
internal sealed class ErpModule : IPlatformModule
{
    public const string ModuleKey = "erp";

    public string Key => ModuleKey;

    public string Name => "进销存";

    public string Description => "产品、仓库库存、销售订单与采购订单：确认订单预留库存，分批发货、分批到货，客户退货和退给供应商，每一笔变化都有流水；库存按移动加权平均计算成本，订单自动算出毛利；商机赢单后可以直接下单，低库存、发货、到货、退货可以触发自动化。";

    public bool IsCore => false;

    public bool EnabledByDefault => true;

    public IReadOnlyList<string> DependsOn { get; } = [CrmModule.ModuleKey];

    public IReadOnlyList<FeatureDefinition> Features { get; } =
    [
        new(ErpFeatures.View, "查看产品、库存与订单", ModuleKey),
        new(ErpFeatures.Sales, "新建、确认、发货、退货、取消销售订单", ModuleKey),
        new(ErpFeatures.Purchase, "新建、下单、收货、退货、取消采购订单", ModuleKey),
        new(ErpFeatures.Inventory, "管理产品与仓库，调整和调拨库存", ModuleKey),
    ];

    public IReadOnlyList<PlatformEventDefinition> Events { get; } =
    [
        new(
            ErpEventKeys.SalesOrderConfirmed,
            "销售订单确认",
            ModuleKey,
            ErpFeatures.View,
            "销售订单确认（库存已预留）时触发。载荷：trigger.order 为订单（id、number、status、companyId、companyName、contactName、dealId、dealTitle、warehouseName、currency、total、orderDate、note、lines：每行 sku、productName、unit、quantity、unitPrice、amount、delivered、returned），trigger.actor 为操作者（kind、id、name）。"),
        new(
            ErpEventKeys.SalesOrderShipped,
            "销售订单发货",
            ModuleKey,
            ErpFeatures.View,
            "销售订单每次发货（库存已扣减）时触发，分批发货时每批触发一次。载荷：trigger.order 为发货后的订单（字段同销售订单确认，另有 shippedAmount、costOfGoods、grossProfit；status 为 partially_shipped 表示还没发完，shipped 表示已全部发完），trigger.document 为本次的发货单（number、warehouseName、amount、note、lines：每行 sku、productName、quantity、unitPrice、amount、unitCost、costAmount），trigger.actor 为操作者。只想在全部发完时处理，可以加条件 trigger.order.status == 'shipped'。"),
        new(
            ErpEventKeys.PurchaseOrderReceived,
            "采购到货",
            ModuleKey,
            ErpFeatures.View,
            "采购订单每次到货入库时触发，分批到货时每批触发一次。载荷：trigger.order 为到货后的采购订单（id、number、status：partially_received 或 received、supplierId、supplierName、warehouseName、currency、total、receivedAmount、lines），trigger.document 为本次的收货单（字段同发货单），trigger.actor 为操作者。"),
        new(
            ErpEventKeys.SalesOrderReturned,
            "客户退货",
            ModuleKey,
            ErpFeatures.View,
            "销售订单登记客户退货（货已退回入库）时触发。载荷：trigger.order 为订单（字段同销售订单发货），trigger.document 为退货单（number、warehouseName、amount 为退款金额、note 为退货原因、lines），trigger.actor 为操作者。"),
        new(
            ErpEventKeys.PurchaseOrderReturned,
            "退货给供应商",
            ModuleKey,
            ErpFeatures.View,
            "采购订单登记退货给供应商（货已出库）时触发。载荷：trigger.order 为采购订单，trigger.document 为退货单（amount 为应退的货款、note 为退货原因、lines），trigger.actor 为操作者。"),
        new(
            ErpEventKeys.StockLow,
            "低库存",
            ModuleKey,
            ErpFeatures.View,
            "产品全部仓库的可用库存（在库减预留）从补货提醒线以上降到以下时触发，每次跌破只触发一次。载荷：trigger.product 为产品（id、sku、name、unit、onHand、reserved、available、reorderPoint、cost、currency），trigger.actor 为造成变化的操作者。可以配合 erp.create_purchase_order 自动起草采购单。"),
    ];
}

/// <summary>
/// 进销存推送给客户端的实体变化类型。
/// </summary>
public static class ErpRealtime
{
    public const string Product = "erp_product";
    public const string Warehouse = "erp_warehouse";
    public const string Stock = "erp_stock";
    public const string SalesOrder = "erp_sales_order";
    public const string PurchaseOrder = "erp_purchase_order";
    public const string Document = "erp_stock_document";
}

/// <summary>
/// 新空间自带一个默认仓库，开箱即可录入库存。
/// </summary>
internal sealed class ErpTenantInitializer(IAppDbContext db) : ITenantInitializer
{
    public const string DefaultWarehouseName = "默认仓库";

    public Task InitializeAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        db.ErpWarehouses.Add(new ErpWarehouse { TenantId = tenantId, Name = DefaultWarehouseName, IsDefault = true });
        return Task.CompletedTask;
    }
}
