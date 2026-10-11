using System.ComponentModel;
using AiNative.Application.Abstractions;
using AiNative.Application.Actions;
using AiNative.Application.Common;
using AiNative.Contracts.Erp;
using AiNative.Domain.Erp;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Erp.Actions;

public sealed class SearchErpProductsInput
{
    [Description("关键字：产品名称或编码（SKU）的一部分；留空列出全部")]
    public string? Query { get; init; }

    [Description("按分类过滤")]
    public string? Category { get; init; }

    [Description("只看低库存（可用数量不高于补货提醒线）的产品")]
    public bool LowStockOnly { get; init; }

    [Description("最多返回条数，默认 20")]
    public int Limit { get; init; } = 20;
}

internal sealed class SearchErpProductsAction(IRequestDispatcher dispatcher) : PlatformAction<SearchErpProductsInput, IReadOnlyList<ErpProductDto>>
{
    public override string Key => "erp.search_products";

    public override string ModuleKey => ErpModule.ModuleKey;

    public override string Title => "搜索产品";

    public override string Description => "按名称、编码或分类搜索在售产品，返回售价、采购价和全部仓库合计的在库、预留、可用数量。下单前先用它找到产品编码；回答「还有多少货」「哪些要补货」时使用（lowStockOnly）。";

    public override string RequiredFeature => ErpFeatures.View;

    public override bool IsMutation => false;

    protected override async Task<IReadOnlyList<ErpProductDto>> ExecuteAsync(SearchErpProductsInput input, ActionContext context, CancellationToken cancellationToken)
        => (await dispatcher.SendAsync(
            new ListErpProductsQuery(input.Query, input.Category, input.LowStockOnly, false, 1, Math.Clamp(input.Limit, 1, 100)),
            cancellationToken).ConfigureAwait(false)).Items;
}

public sealed class GetErpProductStockInput
{
    [Description("产品编码（SKU），与 productId 二选一")]
    public string? Sku { get; init; }

    [Description("产品 ID")]
    public Guid? ProductId { get; init; }
}

internal sealed class GetErpProductStockAction(IRequestDispatcher dispatcher, ErpRefs refs) : PlatformAction<GetErpProductStockInput, ErpProductDetailDto>
{
    public override string Key => "erp.get_product_stock";

    public override string ModuleKey => ErpModule.ModuleKey;

    public override string Title => "查看产品库存";

    public override string Description => "查看一个产品在各仓库的在库、预留、可用数量，以及最近 50 条出入库流水（采购到货、销售发货、调整、调拨）。";

    public override string RequiredFeature => ErpFeatures.View;

    public override bool IsMutation => false;

    protected override async Task<ErpProductDetailDto> ExecuteAsync(GetErpProductStockInput input, ActionContext context, CancellationToken cancellationToken)
        => await dispatcher.SendAsync(
            new GetErpProductQuery(await refs.ProductAsync(input.ProductId, input.Sku, "sku", cancellationToken).ConfigureAwait(false)),
            cancellationToken).ConfigureAwait(false);
}

public sealed class GetErpSummaryInput;

internal sealed class GetErpSummaryAction(IRequestDispatcher dispatcher) : PlatformAction<GetErpSummaryInput, ErpSummaryDto>
{
    public override string Key => "erp.get_summary";

    public override string ModuleKey => ErpModule.ModuleKey;

    public override string Title => "进销存概况";

    public override string Description => "进销存概况：本月发货金额与到货金额、草稿和待发货的销售订单数、待发货金额、未完成的采购订单数、在售产品数和低库存产品数。回答「这个月卖了多少」「有多少单要发」这类问题时使用。不同货币分别合计，不做换算。";

    public override string RequiredFeature => ErpFeatures.View;

    public override bool IsMutation => false;

    protected override Task<ErpSummaryDto> ExecuteAsync(GetErpSummaryInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(new GetErpSummaryQuery(), cancellationToken);
}

public sealed class ErpOrderLineInput
{
    [Description("产品编码（SKU），与 productId 二选一")]
    public string? Sku { get; init; }

    [Description("产品 ID")]
    public Guid? ProductId { get; init; }

    [Description("数量（按产品的计量单位），大于 0")]
    public required decimal Quantity { get; init; }

    [Description("单价；省略时销售订单用产品售价、采购订单用参考采购价")]
    public decimal? UnitPrice { get; init; }
}

public sealed class ListErpSalesOrdersInput
{
    [Description("按状态过滤：draft 草稿、confirmed 已确认待发货、shipped 已发货、cancelled 已取消")]
    public ErpSalesOrderStatus? Status { get; init; }

    [Description("只看某个客户的订单（客户 ID）")]
    public Guid? CompanyId { get; init; }

    [Description("只看某个商机生成的订单（商机 ID）")]
    public Guid? DealId { get; init; }

    [Description("订单号或客户名称关键字")]
    public string? Search { get; init; }

    [Description("最多返回条数，默认 20")]
    public int Limit { get; init; } = 20;
}

internal sealed class ListErpSalesOrdersAction(IRequestDispatcher dispatcher) : PlatformAction<ListErpSalesOrdersInput, IReadOnlyList<ErpSalesOrderDto>>
{
    public override string Key => "erp.list_sales_orders";

    public override string ModuleKey => ErpModule.ModuleKey;

    public override string Title => "查询销售订单";

    public override string Description => "按状态、客户、商机或订单号查询销售订单，最新的在前，包含订单行。";

    public override string RequiredFeature => ErpFeatures.View;

    public override bool IsMutation => false;

    protected override async Task<IReadOnlyList<ErpSalesOrderDto>> ExecuteAsync(ListErpSalesOrdersInput input, ActionContext context, CancellationToken cancellationToken)
        => (await dispatcher.SendAsync(
            new ListErpSalesOrdersQuery(input.Search, input.Status, input.CompanyId, input.DealId, null, 1, Math.Clamp(input.Limit, 1, 100)),
            cancellationToken).ConfigureAwait(false)).Items;
}

public sealed class ErpOrderRefInput
{
    [Description("订单号（例如 SO-0001、PO-0001）或订单 ID")]
    public required string Order { get; init; }
}

internal sealed class GetErpSalesOrderAction(IRequestDispatcher dispatcher, ErpRefs refs) : PlatformAction<ErpOrderRefInput, ErpSalesOrderDto>
{
    public override string Key => "erp.get_sales_order";

    public override string ModuleKey => ErpModule.ModuleKey;

    public override string Title => "查看销售订单";

    public override string Description => "按订单号查看销售订单：客户、来源商机、发货仓库、订单行、金额和状态。";

    public override string RequiredFeature => ErpFeatures.View;

    public override bool IsMutation => false;

    protected override async Task<ErpSalesOrderDto> ExecuteAsync(ErpOrderRefInput input, ActionContext context, CancellationToken cancellationToken)
        => await dispatcher.SendAsync(new GetErpSalesOrderQuery(await refs.SalesOrderAsync(input.Order, cancellationToken).ConfigureAwait(false)), cancellationToken)
            .ConfigureAwait(false);
}

public sealed class CreateErpSalesOrderInput
{
    [Description("来源商机 ID；填了它可以省略客户、联系人和货币，自动从商机带出")]
    public Guid? DealId { get; init; }

    [Description("客户 ID（客户管理里的客户）")]
    public Guid? CompanyId { get; init; }

    [Description("联系人 ID")]
    public Guid? ContactId { get; init; }

    [Description("发货仓库名称；省略为默认仓库")]
    public string? Warehouse { get; init; }

    [Description("货币，例如 CNY；省略时跟随商机，没有商机为 CNY")]
    public string? Currency { get; init; }

    [Description("备注：交货要求、付款条件等")]
    public string Note { get; init; } = string.Empty;

    [Description("订单行，至少一行")]
    public required List<ErpOrderLineInput> Lines { get; init; }

    [Description("建好后立即确认并预留库存；库存不够时整单不建并返回缺货明细。默认只建草稿")]
    public bool Confirm { get; init; }
}

internal sealed class CreateErpSalesOrderAction(IRequestDispatcher dispatcher, ErpRefs refs) : PlatformAction<CreateErpSalesOrderInput, ErpSalesOrderDto>
{
    public override string Key => "erp.create_sales_order";

    public override string ModuleKey => ErpModule.ModuleKey;

    public override string Title => "新建销售订单";

    public override string Description => "新建销售订单（默认是草稿，不占库存）。商机赢单后用 dealId 直接下单；产品用编码指定，先用 erp.search_products 查编码和可用数量。确认后才预留库存，发货用 erp.ship_sales_order。";

    public override string RequiredFeature => ErpFeatures.Sales;

    protected override async Task<ErpSalesOrderDto> ExecuteAsync(CreateErpSalesOrderInput input, ActionContext context, CancellationToken cancellationToken)
        => await dispatcher.SendAsync(new CreateErpSalesOrderCommand(new SaveErpSalesOrderRequest
        {
            DealId = input.DealId,
            CompanyId = input.CompanyId,
            ContactId = input.ContactId,
            WarehouseId = await refs.WarehouseAsync(input.Warehouse, cancellationToken).ConfigureAwait(false),
            Currency = input.Currency,
            Note = input.Note,
            Lines = await refs.LinesAsync(input.Lines, cancellationToken).ConfigureAwait(false),
        }, input.Confirm), cancellationToken).ConfigureAwait(false);
}

internal sealed class ConfirmErpSalesOrderAction(IRequestDispatcher dispatcher, ErpRefs refs) : PlatformAction<ErpOrderRefInput, ErpSalesOrderDto>
{
    public override string Key => "erp.confirm_sales_order";

    public override string ModuleKey => ErpModule.ModuleKey;

    public override string Title => "确认销售订单";

    public override string Description => "确认草稿销售订单并预留库存。任何一行可用数量不够都会整单失败，错误信息列出所有缺货的产品，可以据此起草采购单。";

    public override string RequiredFeature => ErpFeatures.Sales;

    protected override async Task<ErpSalesOrderDto> ExecuteAsync(ErpOrderRefInput input, ActionContext context, CancellationToken cancellationToken)
    {
        var orderId = await refs.SalesOrderAsync(input.Order, cancellationToken).ConfigureAwait(false);
        return await dispatcher.SendAsync(new ConfirmErpSalesOrderCommand(orderId), cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class ShipErpSalesOrderAction(IRequestDispatcher dispatcher, ErpRefs refs) : PlatformAction<ErpOrderRefInput, ErpSalesOrderDto>
{
    public override string Key => "erp.ship_sales_order";

    public override string ModuleKey => ErpModule.ModuleKey;

    public override string Title => "销售订单发货";

    public override string Description => "登记已确认的销售订单已经发货：扣减库存并记出库流水。只在货确实发出后使用。";

    public override string RequiredFeature => ErpFeatures.Sales;

    protected override async Task<ErpSalesOrderDto> ExecuteAsync(ErpOrderRefInput input, ActionContext context, CancellationToken cancellationToken)
    {
        var orderId = await refs.SalesOrderAsync(input.Order, cancellationToken).ConfigureAwait(false);
        return await dispatcher.SendAsync(new ShipErpSalesOrderCommand(orderId, context.RunId), cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class CancelErpSalesOrderAction(IRequestDispatcher dispatcher, ErpRefs refs) : PlatformAction<ErpOrderRefInput, ErpSalesOrderDto>
{
    public override string Key => "erp.cancel_sales_order";

    public override string ModuleKey => ErpModule.ModuleKey;

    public override string Title => "取消销售订单";

    public override string Description => "取消草稿或已确认的销售订单，已确认的会释放预留的库存。已发货的订单不能取消。智能体发起时需要人工审批。";

    public override string RequiredFeature => ErpFeatures.Sales;

    public override ApprovalPolicy ApprovalPolicy => ApprovalPolicy.RequiredForAgents;

    protected override async Task<ErpSalesOrderDto> ExecuteAsync(ErpOrderRefInput input, ActionContext context, CancellationToken cancellationToken)
    {
        var orderId = await refs.SalesOrderAsync(input.Order, cancellationToken).ConfigureAwait(false);
        return await dispatcher.SendAsync(new CancelErpSalesOrderCommand(orderId), cancellationToken).ConfigureAwait(false);
    }
}

public sealed class ListErpPurchaseOrdersInput
{
    [Description("按状态过滤：draft 草稿、ordered 已下单待到货、received 已到货、cancelled 已取消")]
    public ErpPurchaseOrderStatus? Status { get; init; }

    [Description("只看某个供应商的订单（客户管理里的客户 ID）")]
    public Guid? SupplierId { get; init; }

    [Description("订单号或供应商名称关键字")]
    public string? Search { get; init; }

    [Description("最多返回条数，默认 20")]
    public int Limit { get; init; } = 20;
}

internal sealed class ListErpPurchaseOrdersAction(IRequestDispatcher dispatcher) : PlatformAction<ListErpPurchaseOrdersInput, IReadOnlyList<ErpPurchaseOrderDto>>
{
    public override string Key => "erp.list_purchase_orders";

    public override string ModuleKey => ErpModule.ModuleKey;

    public override string Title => "查询采购订单";

    public override string Description => "按状态、供应商或订单号查询采购订单，最新的在前，包含订单行。起草补货采购单前先看看是否已经有在途的订单。";

    public override string RequiredFeature => ErpFeatures.View;

    public override bool IsMutation => false;

    protected override async Task<IReadOnlyList<ErpPurchaseOrderDto>> ExecuteAsync(ListErpPurchaseOrdersInput input, ActionContext context, CancellationToken cancellationToken)
        => (await dispatcher.SendAsync(
            new ListErpPurchaseOrdersQuery(input.Search, input.Status, input.SupplierId, 1, Math.Clamp(input.Limit, 1, 100)),
            cancellationToken).ConfigureAwait(false)).Items;
}

public sealed class CreateErpPurchaseOrderInput
{
    [Description("供应商 ID（客户管理里的客户；没有时先用 crm.create_company 建）")]
    public required Guid SupplierId { get; init; }

    [Description("收货仓库名称；省略为默认仓库")]
    public string? Warehouse { get; init; }

    [Description("货币，默认 CNY")]
    public string Currency { get; init; } = "CNY";

    [Description("预计到货日期，例如 2026-11-01")]
    public DateOnly? ExpectedDate { get; init; }

    [Description("备注")]
    public string Note { get; init; } = string.Empty;

    [Description("订单行，至少一行")]
    public required List<ErpOrderLineInput> Lines { get; init; }
}

internal sealed class CreateErpPurchaseOrderAction(IRequestDispatcher dispatcher, ErpRefs refs) : PlatformAction<CreateErpPurchaseOrderInput, ErpPurchaseOrderDto>
{
    public override string Key => "erp.create_purchase_order";

    public override string ModuleKey => ErpModule.ModuleKey;

    public override string Title => "起草采购订单";

    public override string Description => "起草采购订单（草稿，不会发给供应商）。补货时数量可以参考补货提醒线与当前可用数量的差额；单价省略时用产品的参考采购价。正式下单用 erp.place_purchase_order。";

    public override string RequiredFeature => ErpFeatures.Purchase;

    protected override async Task<ErpPurchaseOrderDto> ExecuteAsync(CreateErpPurchaseOrderInput input, ActionContext context, CancellationToken cancellationToken)
        => await dispatcher.SendAsync(new CreateErpPurchaseOrderCommand(new SaveErpPurchaseOrderRequest
        {
            SupplierId = input.SupplierId,
            WarehouseId = await refs.WarehouseAsync(input.Warehouse, cancellationToken).ConfigureAwait(false),
            Currency = input.Currency,
            ExpectedDate = input.ExpectedDate,
            Note = input.Note,
            Lines = await refs.LinesAsync(input.Lines, cancellationToken).ConfigureAwait(false),
        }), cancellationToken).ConfigureAwait(false);
}

internal sealed class PlaceErpPurchaseOrderAction(IRequestDispatcher dispatcher, ErpRefs refs) : PlatformAction<ErpOrderRefInput, ErpPurchaseOrderDto>
{
    public override string Key => "erp.place_purchase_order";

    public override string ModuleKey => ErpModule.ModuleKey;

    public override string Title => "采购下单";

    public override string Description => "把草稿采购订单正式下单（表示已发给供应商，之后不能修改）。涉及付款，智能体发起时需要人工审批。";

    public override string RequiredFeature => ErpFeatures.Purchase;

    public override ApprovalPolicy ApprovalPolicy => ApprovalPolicy.RequiredForAgents;

    protected override async Task<ErpPurchaseOrderDto> ExecuteAsync(ErpOrderRefInput input, ActionContext context, CancellationToken cancellationToken)
    {
        var orderId = await refs.PurchaseOrderAsync(input.Order, cancellationToken).ConfigureAwait(false);
        return await dispatcher.SendAsync(new PlaceErpPurchaseOrderCommand(orderId), cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class ReceiveErpPurchaseOrderAction(IRequestDispatcher dispatcher, ErpRefs refs) : PlatformAction<ErpOrderRefInput, ErpPurchaseOrderDto>
{
    public override string Key => "erp.receive_purchase_order";

    public override string ModuleKey => ErpModule.ModuleKey;

    public override string Title => "采购到货";

    public override string Description => "登记已下单的采购订单全部到货：每行入库并记入库流水。只在货确实到了以后使用。";

    public override string RequiredFeature => ErpFeatures.Purchase;

    protected override async Task<ErpPurchaseOrderDto> ExecuteAsync(ErpOrderRefInput input, ActionContext context, CancellationToken cancellationToken)
    {
        var orderId = await refs.PurchaseOrderAsync(input.Order, cancellationToken).ConfigureAwait(false);
        return await dispatcher.SendAsync(new ReceiveErpPurchaseOrderCommand(orderId, context.RunId), cancellationToken).ConfigureAwait(false);
    }
}

public sealed class AdjustErpStockInput
{
    [Description("产品编码（SKU），与 productId 二选一")]
    public string? Sku { get; init; }

    [Description("产品 ID")]
    public Guid? ProductId { get; init; }

    [Description("仓库名称；省略为默认仓库")]
    public string? Warehouse { get; init; }

    [Description("变化数量：入库为正、出库为负，与 countedQuantity 二选一")]
    public decimal? Quantity { get; init; }

    [Description("盘点后的实际数量，与 quantity 二选一")]
    public decimal? CountedQuantity { get; init; }

    [Description("原因，例如期初录入、盘点、报损")]
    public required string Note { get; init; }
}

internal sealed class AdjustErpStockAction(IRequestDispatcher dispatcher, ErpRefs refs) : PlatformAction<AdjustErpStockInput, ErpStockLevelDto>
{
    public override string Key => "erp.adjust_stock";

    public override string ModuleKey => ErpModule.ModuleKey;

    public override string Title => "调整库存";

    public override string Description => "手工调整库存（期初录入、盘点、报损），会记一条调整流水。采购到货和销售发货不要用它，分别用 erp.receive_purchase_order 和 erp.ship_sales_order。智能体发起时需要人工审批。";

    public override string RequiredFeature => ErpFeatures.Inventory;

    public override ApprovalPolicy ApprovalPolicy => ApprovalPolicy.RequiredForAgents;

    protected override async Task<ErpStockLevelDto> ExecuteAsync(AdjustErpStockInput input, ActionContext context, CancellationToken cancellationToken)
        => await dispatcher.SendAsync(new AdjustErpStockCommand(new AdjustErpStockRequest
        {
            ProductId = await refs.ProductAsync(input.ProductId, input.Sku, "sku", cancellationToken).ConfigureAwait(false),
            WarehouseId = await refs.WarehouseAsync(input.Warehouse, cancellationToken).ConfigureAwait(false),
            Quantity = input.Quantity,
            CountedQuantity = input.CountedQuantity,
            Note = input.Note,
        }, context.RunId), cancellationToken).ConfigureAwait(false);
}

public sealed class CreateErpProductInput
{
    [Description("产品编码（SKU），空间内唯一，不能含空格；保存时转为大写")]
    public required string Sku { get; init; }

    [Description("产品名称")]
    public required string Name { get; init; }

    [Description("计量单位，例如件、箱、千克、小时，默认件")]
    public string Unit { get; init; } = "件";

    [Description("分类")]
    public string Category { get; init; } = string.Empty;

    [Description("描述：规格、型号等")]
    public string Description { get; init; } = string.Empty;

    [Description("标准售价")]
    public decimal? Price { get; init; }

    [Description("参考采购价")]
    public decimal? Cost { get; init; }

    [Description("价格的货币，默认 CNY")]
    public string Currency { get; init; } = "CNY";

    [Description("是否管理库存；服务类产品设为 false，下单不占库存。默认 true")]
    public bool TrackInventory { get; init; } = true;

    [Description("补货提醒线：可用数量降到它或以下时触发低库存事件")]
    public decimal? ReorderPoint { get; init; }
}

internal sealed class CreateErpProductAction(IRequestDispatcher dispatcher) : PlatformAction<CreateErpProductInput, ErpProductDto>
{
    public override string Key => "erp.create_product";

    public override string ModuleKey => ErpModule.ModuleKey;

    public override string Title => "新建产品";

    public override string Description => "新建产品。新建前先用 erp.search_products 确认还没有这个产品；新产品库存为 0，期初数量用 erp.adjust_stock 录入。";

    public override string RequiredFeature => ErpFeatures.Inventory;

    protected override Task<ErpProductDto> ExecuteAsync(CreateErpProductInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(new CreateErpProductCommand(new SaveErpProductRequest
        {
            Sku = input.Sku,
            Name = input.Name,
            Unit = input.Unit,
            Category = input.Category,
            Description = input.Description,
            Price = input.Price,
            Cost = input.Cost,
            Currency = input.Currency,
            TrackInventory = input.TrackInventory,
            ReorderPoint = input.ReorderPoint,
        }), cancellationToken);
}

/// <summary>
/// 动作里用编码、名称、订单号指代记录，这里统一解析成 ID，找不到时给出模型能据此改正的提示。
/// 直接在当前空间里查，不经过需要查看权限的查询：动作入口已按动作声明的权限检查过，
/// 例如只有库存权限的成员用编码调整库存，和用产品 ID 一样能执行。
/// </summary>
internal sealed class ErpRefs(IAppDbContext db)
{
    public async Task<Guid> ProductAsync(Guid? productId, string? sku, string field, CancellationToken cancellationToken)
    {
        if (productId is { } id)
        {
            return id;
        }

        if (string.IsNullOrWhiteSpace(sku))
        {
            throw Invalid(field, "请提供产品编码（sku）或产品 ID。");
        }

        var normalized = ErpProductHandlers.NormalizeSku(sku);
        return await db.ErpProducts.AsNoTracking().Where(p => p.Sku == normalized).Select(p => (Guid?)p.Id)
                   .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
               ?? throw Invalid(field, $"找不到编码为「{sku.Trim()}」的产品，可以先用 erp.search_products 按名称搜索。");
    }

    public async Task<IReadOnlyList<ErpOrderLineRequest>> LinesAsync(IReadOnlyList<ErpOrderLineInput> lines, CancellationToken cancellationToken)
    {
        var result = new List<ErpOrderLineRequest>(lines.Count);
        foreach (var line in lines)
        {
            result.Add(new ErpOrderLineRequest
            {
                ProductId = await ProductAsync(line.ProductId, line.Sku, "lines", cancellationToken).ConfigureAwait(false),
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
            });
        }

        return result;
    }

    public async Task<Guid?> WarehouseAsync(string? name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var warehouses = await db.ErpWarehouses.AsNoTracking().OrderByDescending(w => w.IsDefault).ThenBy(w => w.CreatedAt)
            .Select(w => new { w.Id, w.Name }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var wanted = name.Trim();
        return warehouses.FirstOrDefault(w => string.Equals(w.Name, wanted, StringComparison.OrdinalIgnoreCase))?.Id
               ?? throw Invalid("warehouse", $"没有名为「{wanted}」的仓库，可选：{string.Join("、", warehouses.Select(w => w.Name))}。");
    }

    /// <summary>
    /// 订单号（例如 SO-0001，不区分大小写）或订单 ID。
    /// </summary>
    public async Task<Guid> SalesOrderAsync(string reference, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(reference, out var id))
        {
            return id;
        }

        var number = reference.Trim().ToUpperInvariant();
        return await db.ErpSalesOrders.AsNoTracking().Where(o => o.Number == number).Select(o => (Guid?)o.Id)
                   .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
               ?? throw new NotFoundException("erp_sales_order", number);
    }

    /// <summary>
    /// 订单号（例如 PO-0001，不区分大小写）或订单 ID。
    /// </summary>
    public async Task<Guid> PurchaseOrderAsync(string reference, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(reference, out var id))
        {
            return id;
        }

        var number = reference.Trim().ToUpperInvariant();
        return await db.ErpPurchaseOrders.AsNoTracking().Where(o => o.Number == number).Select(o => (Guid?)o.Id)
                   .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
               ?? throw new NotFoundException("erp_purchase_order", number);
    }

    private static ValidationFailedException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}
