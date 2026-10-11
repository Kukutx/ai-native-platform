using AiNative.Application.Erp;
using AiNative.Contracts.Common;
using AiNative.Contracts.Erp;
using AiNative.Domain.Erp;
using Microsoft.AspNetCore.Mvc;

namespace AiNative.Api.Controllers;

[Route("api/erp/products")]
public sealed class ErpProductsController : ApiControllerBase
{
    /// <summary>
    /// 产品列表，按编码排序；search 匹配名称和编码，默认不含停用的产品。库存数量为全部仓库合计。
    /// </summary>
    [HttpGet]
    public Task<PagedResult<ErpProductDto>> List(
        [FromQuery] string? search,
        [FromQuery] string? category,
        [FromQuery] bool lowStockOnly = false,
        [FromQuery] bool includeInactive = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
        => SendAsync(new ListErpProductsQuery(search, category, lowStockOnly, includeInactive, page, pageSize));

    /// <summary>
    /// 已用过的产品分类，供筛选和录入时选择。
    /// </summary>
    [HttpGet("categories")]
    public Task<IReadOnlyList<string>> Categories() => SendAsync(new ListErpCategoriesQuery());

    /// <summary>
    /// 产品详情：各仓库库存与最近 50 条出入库流水。
    /// </summary>
    [HttpGet("{id:guid}")]
    public Task<ErpProductDetailDto> Get(Guid id) => SendAsync(new GetErpProductQuery(id));

    [HttpPost]
    public Task<ErpProductDto> Create(SaveErpProductRequest request) => SendAsync(new CreateErpProductCommand(request));

    [HttpPut("{id:guid}")]
    public Task<ErpProductDto> Update(Guid id, SaveErpProductRequest request) => SendAsync(new UpdateErpProductCommand(id, request));

    /// <summary>
    /// 删除从没用过的产品；已有订单或库存记录时返回 409，可以改为停用。
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await SendAsync(new DeleteErpProductCommand(id)).ConfigureAwait(false);
        return NoContent();
    }
}

[Route("api/erp/warehouses")]
public sealed class ErpWarehousesController : ApiControllerBase
{
    /// <summary>
    /// 仓库列表，默认仓库在前。
    /// </summary>
    [HttpGet]
    public Task<IReadOnlyList<ErpWarehouseDto>> List() => SendAsync(new ListErpWarehousesQuery());

    [HttpPost]
    public Task<ErpWarehouseDto> Create(SaveErpWarehouseRequest request) => SendAsync(new CreateErpWarehouseCommand(request));

    [HttpPut("{id:guid}")]
    public Task<ErpWarehouseDto> Update(Guid id, SaveErpWarehouseRequest request) => SendAsync(new UpdateErpWarehouseCommand(id, request));

    /// <summary>
    /// 删除空仓库；默认仓库、还有库存或用过的仓库返回 409。
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await SendAsync(new DeleteErpWarehouseCommand(id)).ConfigureAwait(false);
        return NoContent();
    }
}

[Route("api/erp/stock")]
public sealed class ErpStockController : ApiControllerBase
{
    /// <summary>
    /// 各产品在各仓库的库存（只含管库存的产品），按编码排序。
    /// </summary>
    [HttpGet]
    public Task<PagedResult<ErpStockLevelDto>> Levels(
        [FromQuery] Guid? warehouseId,
        [FromQuery] Guid? productId,
        [FromQuery] string? search,
        [FromQuery] bool lowStockOnly = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
        => SendAsync(new ListErpStockLevelsQuery(warehouseId, productId, search, lowStockOnly, page, pageSize));

    /// <summary>
    /// 出入库流水，最新的在前。
    /// </summary>
    [HttpGet("movements")]
    public Task<PagedResult<ErpStockMovementDto>> Movements(
        [FromQuery] Guid? productId,
        [FromQuery] Guid? warehouseId,
        [FromQuery] ErpStockMovementKind? kind,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
        => SendAsync(new ListErpStockMovementsQuery(productId, warehouseId, kind, page, pageSize));

    /// <summary>
    /// 手工调整（期初、盘点、报损）。在库不够减时返回 409。
    /// </summary>
    [HttpPost("adjust")]
    public Task<ErpStockLevelDto> Adjust(AdjustErpStockRequest request) => SendAsync(new AdjustErpStockCommand(request));

    /// <summary>
    /// 仓库之间调拨，只能调出可用数量。返回调出、调入两个仓库调拨后的库存。
    /// </summary>
    [HttpPost("transfer")]
    public Task<IReadOnlyList<ErpStockLevelDto>> Transfer(TransferErpStockRequest request) => SendAsync(new TransferErpStockCommand(request));
}

[Route("api/erp/sales-orders")]
public sealed class ErpSalesOrdersController : ApiControllerBase
{
    /// <summary>
    /// 销售订单，最新的在前；search 匹配订单号和客户名称。
    /// </summary>
    [HttpGet]
    public Task<PagedResult<ErpSalesOrderDto>> List(
        [FromQuery] string? search,
        [FromQuery] ErpSalesOrderStatus? status,
        [FromQuery] Guid? companyId,
        [FromQuery] Guid? dealId,
        [FromQuery] Guid? ownerUserId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
        => SendAsync(new ListErpSalesOrdersQuery(search, status, companyId, dealId, ownerUserId, page, pageSize));

    [HttpGet("{id:guid}")]
    public Task<ErpSalesOrderDto> Get(Guid id) => SendAsync(new GetErpSalesOrderQuery(id));

    /// <summary>
    /// 新建销售订单（草稿）。confirm=true 时建好立即确认并预留库存，库存不够返回 409 且不建单。
    /// </summary>
    [HttpPost]
    public Task<ErpSalesOrderDto> Create(SaveErpSalesOrderRequest request, [FromQuery] bool confirm = false)
        => SendAsync(new CreateErpSalesOrderCommand(request, confirm));

    /// <summary>
    /// 修改草稿订单；不是草稿或已被别人修改时返回 409。
    /// </summary>
    [HttpPut("{id:guid}")]
    public Task<ErpSalesOrderDto> Update(Guid id, SaveErpSalesOrderRequest request) => SendAsync(new UpdateErpSalesOrderCommand(id, request));

    /// <summary>
    /// 确认订单并预留库存；可用数量不够时返回 409，列出缺货的产品。
    /// </summary>
    [HttpPost("{id:guid}/confirm")]
    public Task<ErpSalesOrderDto> Confirm(Guid id) => SendAsync(new ConfirmErpSalesOrderCommand(id));

    /// <summary>
    /// 发货：扣减库存并记出库流水。
    /// </summary>
    [HttpPost("{id:guid}/ship")]
    public Task<ErpSalesOrderDto> Ship(Guid id) => SendAsync(new ShipErpSalesOrderCommand(id));

    /// <summary>
    /// 取消草稿或已确认的订单（释放预留）。
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    public Task<ErpSalesOrderDto> Cancel(Guid id) => SendAsync(new CancelErpSalesOrderCommand(id));

    /// <summary>
    /// 删除草稿或已取消的订单。
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await SendAsync(new DeleteErpSalesOrderCommand(id)).ConfigureAwait(false);
        return NoContent();
    }
}

[Route("api/erp/purchase-orders")]
public sealed class ErpPurchaseOrdersController : ApiControllerBase
{
    /// <summary>
    /// 采购订单，最新的在前；search 匹配订单号和供应商名称。
    /// </summary>
    [HttpGet]
    public Task<PagedResult<ErpPurchaseOrderDto>> List(
        [FromQuery] string? search,
        [FromQuery] ErpPurchaseOrderStatus? status,
        [FromQuery] Guid? supplierId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
        => SendAsync(new ListErpPurchaseOrdersQuery(search, status, supplierId, page, pageSize));

    [HttpGet("{id:guid}")]
    public Task<ErpPurchaseOrderDto> Get(Guid id) => SendAsync(new GetErpPurchaseOrderQuery(id));

    [HttpPost]
    public Task<ErpPurchaseOrderDto> Create(SaveErpPurchaseOrderRequest request) => SendAsync(new CreateErpPurchaseOrderCommand(request));

    [HttpPut("{id:guid}")]
    public Task<ErpPurchaseOrderDto> Update(Guid id, SaveErpPurchaseOrderRequest request) => SendAsync(new UpdateErpPurchaseOrderCommand(id, request));

    /// <summary>
    /// 下单（已发给供应商），之后不能再修改。
    /// </summary>
    [HttpPost("{id:guid}/place")]
    public Task<ErpPurchaseOrderDto> Place(Guid id) => SendAsync(new PlaceErpPurchaseOrderCommand(id));

    /// <summary>
    /// 到货：全部入库并记入库流水。
    /// </summary>
    [HttpPost("{id:guid}/receive")]
    public Task<ErpPurchaseOrderDto> Receive(Guid id) => SendAsync(new ReceiveErpPurchaseOrderCommand(id));

    [HttpPost("{id:guid}/cancel")]
    public Task<ErpPurchaseOrderDto> Cancel(Guid id) => SendAsync(new CancelErpPurchaseOrderCommand(id));

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await SendAsync(new DeleteErpPurchaseOrderCommand(id)).ConfigureAwait(false);
        return NoContent();
    }
}

[Route("api/erp/summary")]
public sealed class ErpSummaryController : ApiControllerBase
{
    /// <summary>
    /// 进销存概况：本月发货与到货金额、待处理的订单、低库存产品数。
    /// </summary>
    [HttpGet]
    public Task<ErpSummaryDto> Get() => SendAsync(new GetErpSummaryQuery());
}
