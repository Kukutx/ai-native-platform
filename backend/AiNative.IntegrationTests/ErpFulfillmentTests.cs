using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiNative.Application.Common;
using AiNative.Contracts.Actions;
using AiNative.Contracts.Common;
using AiNative.Contracts.Crm;
using AiNative.Contracts.Erp;
using AiNative.Contracts.Platform;
using AiNative.Contracts.Work;
using AiNative.Contracts.Workflows;
using AiNative.Domain.Erp;
using AiNative.Domain.Workflows;
using AiNative.IntegrationTests.Infrastructure;

namespace AiNative.IntegrationTests;

/// <summary>
/// 分批发货与收货、退货、移动加权平均成本和毛利。
/// </summary>
public sealed class ErpFulfillmentTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Partial_shipments_keep_the_rest_reserved_and_closing_releases_it()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        await EventWorkflowAsync(client, "erp.sales_order_shipped", "{{ trigger.document.number }} {{ trigger.order.status }} {{ trigger.document.amount }}");
        var widget = await ProductAsync(client, "W-1", "螺丝", price: 10, cost: 6);
        var service = await PostAsync<ErpProductDto>(client, "/api/erp/products", new SaveErpProductRequest { Sku = "SVC", Name = "安装服务", Unit = "次", Price = 100, Cost = 30, TrackInventory = false });

        // 仓库里还没有成本时，期初录入按产品的参考采购价计成本。
        var opening = await AdjustAsync(client, widget.Id, 20);
        (opening.AverageCost, opening.StockValue, opening.Currency).Should().Be((6m, 120m, "CNY"));

        var order = await PostAsync<ErpSalesOrderDto>(client, "/api/erp/sales-orders?confirm=true", new SaveErpSalesOrderRequest { Lines = [Line(widget.Id, 10), Line(service.Id, 1)] });
        var first = await PostAsync<ErpSalesOrderDto>(client, $"/api/erp/sales-orders/{order.Id}/ship", new ErpDeliveryRequest
        {
            Lines = [Quantity(widget.Id, 4), Quantity(service.Id, 0)],
            Note = "第一批",
        });
        first.Status.Should().Be(ErpSalesOrderStatus.PartiallyShipped);
        first.ShippedAt.Should().BeNull();
        first.Lines.Select(l => l.Delivered).Should().Equal(4m, 0m);
        (first.ShippedAmount, first.CostOfGoods, first.GrossProfit).Should().Be((40m, 24m, 16m));
        (await ProductDetailAsync(client, widget.Id)).Product.Should().Match<ErpProductDto>(p => p.OnHand == 16 && p.Reserved == 6);

        // 超过未发的数量、订单里没有的产品都会被拒绝；已经发过货的订单不能取消。
        var tooMany = await client.PostAsJsonAsync($"/api/erp/sales-orders/{order.Id}/ship", new ErpDeliveryRequest { Lines = [Quantity(widget.Id, 7)] }, PlatformJson.Options, Ct);
        tooMany.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsAsync(tooMany)).Should().Contain("「螺丝」最多还能发货 6 件。");
        var stranger = await ProductAsync(client, "X-1", "别的产品", price: 1);
        (await client.PostAsJsonAsync($"/api/erp/sales-orders/{order.Id}/ship", new ErpDeliveryRequest { Lines = [Quantity(stranger.Id, 1)] }, PlatformJson.Options, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var cancel = await client.PostAsJsonAsync($"/api/erp/sales-orders/{order.Id}/cancel", new { }, PlatformJson.Options, Ct);
        (await CodeAsync(cancel)).Should().Be("erp_sales_order.status_changed");

        var documents = await GetAsync<PagedResult<ErpStockDocumentDto>>(client, $"/api/erp/stock-documents?salesOrderId={order.Id}");
        var shipment = documents.Items.Should().ContainSingle().Subject;
        shipment.Should().Match<ErpStockDocumentDto>(d => d.Number == "SH-0001" && d.Kind == ErpStockDocumentKind.Shipment && d.Amount == 40 && d.Note == "第一批" && d.OrderNumber == order.Number);
        shipment.Lines.Should().ContainSingle().Which.Should().Match<ErpStockDocumentLineDto>(l => l.Quantity == 4 && l.UnitCost == 6 && l.CostAmount == 24 && l.CostCurrency == "CNY");
        var movement = (await ProductDetailAsync(client, widget.Id)).Movements[0];
        movement.Should().Match<ErpStockMovementDto>(m => m.Kind == ErpStockMovementKind.Sale && m.Quantity == -4 && m.UnitCost == 6 && m.CostAmount == -24 && m.DocumentNumber == "SH-0001");

        // 不带明细时发出剩下的全部；不管库存的服务按参考采购价计成本。
        var rest = await PostAsync<ErpSalesOrderDto>(client, $"/api/erp/sales-orders/{order.Id}/ship", new { });
        rest.Status.Should().Be(ErpSalesOrderStatus.Shipped);
        rest.ShippedAt.Should().NotBeNull();
        (rest.ShippedAmount, rest.CostOfGoods, rest.GrossProfit).Should().Be((200m, 90m, 110m));
        (await GetAsync<ErpStockDocumentDto>(client, $"/api/erp/stock-documents/{(await GetAsync<PagedResult<ErpStockDocumentDto>>(client, $"/api/erp/stock-documents?salesOrderId={order.Id}")).Items[0].Id}"))
            .Should().Match<ErpStockDocumentDto>(d => d.Number == "SH-0002" && d.Amount == 160);

        // 部分发货后结束发货：剩下的预留释放，订单变为已发货。
        var second = await PostAsync<ErpSalesOrderDto>(client, "/api/erp/sales-orders?confirm=true", new SaveErpSalesOrderRequest { Lines = [Line(widget.Id, 5)] });
        await PostAsync<ErpSalesOrderDto>(client, $"/api/erp/sales-orders/{second.Id}/ship", new ErpDeliveryRequest { Lines = [Quantity(widget.Id, 2)] });
        (await GetAsync<PagedResult<ErpSalesOrderDto>>(client, "/api/erp/sales-orders?pending=true")).Items.Should().ContainSingle().Which.Id.Should().Be(second.Id);
        var notPartial = await client.PostAsJsonAsync($"/api/erp/sales-orders/{order.Id}/close", new { }, PlatformJson.Options, Ct);
        (await CodeAsync(notPartial)).Should().Be("erp_sales_order.status_changed");
        var closed = await PostAsync<ErpSalesOrderDto>(client, $"/api/erp/sales-orders/{second.Id}/close", new { });
        closed.Status.Should().Be(ErpSalesOrderStatus.Shipped);
        closed.Lines[0].Should().Match<ErpOrderLineDto>(l => l.Quantity == 5 && l.Delivered == 2);
        (await ProductDetailAsync(client, widget.Id)).Product.Should().Match<ErpProductDto>(p => p.OnHand == 8 && p.Reserved == 0 && p.StockValue == 48);

        // 每批发货各触发一次事件，载荷带发货后的订单状态和本次的发货单。
        var tasks = await EventuallyAsync(() => GetAsync<PagedResult<WorkItemDto>>(client, "/api/work-items"), r => r.Total == 3);
        tasks.Items.Select(t => t.Title).Should().BeEquivalentTo(["SH-0001 partially_shipped 40", "SH-0002 shipped 160", "SH-0003 partially_shipped 20"]);

        var summary = await GetAsync<ErpSummaryDto>(client, "/api/erp/summary");
        summary.ShippedThisMonth.Should().BeEquivalentTo([new ErpAmountDto("CNY", 220)]);
        summary.GrossProfitThisMonth.Should().BeEquivalentTo([new ErpAmountDto("CNY", 118)]);
        summary.ToShipSalesOrders.Should().Be(0);
        summary.StockValue.Should().BeEquivalentTo([new ErpAmountDto("CNY", 48)]);
    }

    [Fact]
    public async Task Moving_average_cost_follows_receipts_transfers_and_adjustments()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var product = await ProductAsync(client, "A-1", "电机", price: 30, cost: 10);
        var supplier = await PostAsync<CrmCompanyDto>(client, "/api/crm/companies", new SaveCrmCompanyRequest { Name = "华东五金" });

        await ReceiveAllAsync(client, await PlacedOrderAsync(client, supplier.Id, product.Id, 10, 16m - 6));
        (await LevelAsync(client, product.Id)).AverageCost.Should().Be(10);

        // 分批到货：每批按采购单价重算平均成本。
        var po = await PlacedOrderAsync(client, supplier.Id, product.Id, 20, 16);
        var partial = await PostAsync<ErpPurchaseOrderDto>(client, $"/api/erp/purchase-orders/{po.Id}/receive", new ErpDeliveryRequest { Lines = [Quantity(product.Id, 10)] });
        partial.Status.Should().Be(ErpPurchaseOrderStatus.PartiallyReceived);
        partial.ReceivedAmount.Should().Be(160);
        (await LevelAsync(client, product.Id)).AverageCost.Should().Be(13);
        (await GetAsync<PagedResult<ErpPurchaseOrderDto>>(client, "/api/erp/purchase-orders?pending=true")).Items.Should().ContainSingle().Which.Id.Should().Be(po.Id);
        var cancel = await client.PostAsJsonAsync($"/api/erp/purchase-orders/{po.Id}/cancel", new { }, PlatformJson.Options, Ct);
        (await CodeAsync(cancel)).Should().Be("erp_purchase_order.status_changed");

        // 出库按当时的平均成本，平均成本不变。
        var sale = await PostAsync<ErpSalesOrderDto>(client, "/api/erp/sales-orders?confirm=true", new SaveErpSalesOrderRequest { Lines = [Line(product.Id, 5)] });
        var shipped = await PostAsync<ErpSalesOrderDto>(client, $"/api/erp/sales-orders/{sale.Id}/ship", new { });
        (shipped.CostOfGoods, shipped.GrossProfit).Should().Be((65m, 85m));
        (await LevelAsync(client, product.Id)).AverageCost.Should().Be(13);

        var received = await PostAsync<ErpPurchaseOrderDto>(client, $"/api/erp/purchase-orders/{po.Id}/receive", new { });
        received.Status.Should().Be(ErpPurchaseOrderStatus.Received);
        received.Lines[0].Delivered.Should().Be(20);
        (await LevelAsync(client, product.Id)).AverageCost.Should().Be(14.2m);

        // 调拨时成本随货走；调入后再按填写的单位成本加库存。
        var annex = await PostAsync<ErpWarehouseDto>(client, "/api/erp/warehouses", new SaveErpWarehouseRequest { Name = "二号仓" });
        var defaultWarehouse = (await GetAsync<List<ErpWarehouseDto>>(client, "/api/erp/warehouses")).Single(w => w.IsDefault);
        await PostAsync<List<ErpStockLevelDto>>(client, "/api/erp/stock/transfer", new TransferErpStockRequest
        {
            ProductId = product.Id,
            FromWarehouseId = defaultWarehouse.Id,
            ToWarehouseId = annex.Id,
            Quantity = 5,
        });
        var annexLevel = await PostAsync<ErpStockLevelDto>(client, "/api/erp/stock/adjust", new AdjustErpStockRequest
        {
            ProductId = product.Id,
            WarehouseId = annex.Id,
            Quantity = 5,
            UnitCost = 20,
            Note = "补录",
        });
        (annexLevel.OnHand, annexLevel.AverageCost, annexLevel.StockValue).Should().Be((10m, 17.1m, 171m));
        var detail = await ProductDetailAsync(client, product.Id);
        detail.Product.Should().Match<ErpProductDto>(p => p.OnHand == 30 && p.StockValue == 455 && p.AverageCost == 15.166667m);
        detail.Movements.First(m => m.Kind == ErpStockMovementKind.TransferIn).Should().Match<ErpStockMovementDto>(m => m.UnitCost == 14.2m && m.CostAmount == 71);

        // 盘亏按平均成本出库。
        await AdjustAsync(client, product.Id, -2);
        (await ProductDetailAsync(client, product.Id)).Movements[0].CostAmount.Should().Be(-28.4m);

        // 采购货币与产品不同（暂不换算汇率）时按参考采购价入库。
        var usd = await PostAsync<ErpPurchaseOrderDto>(client, "/api/erp/purchase-orders", new SaveErpPurchaseOrderRequest
        {
            SupplierId = supplier.Id,
            Currency = "USD",
            Lines = [new ErpOrderLineRequest { ProductId = product.Id, Quantity = 1, UnitPrice = 2 }],
        });
        await PostAsync<ErpPurchaseOrderDto>(client, $"/api/erp/purchase-orders/{usd.Id}/place", new { });
        await ReceiveAllAsync(client, usd);
        (await LevelAsync(client, product.Id)).AverageCost.Should().Be(13.978947m);

        // 还有库存时不能改产品货币，否则已有的成本对不上。
        var locked = await client.PutAsJsonAsync($"/api/erp/products/{product.Id}", new SaveErpProductRequest { Sku = "A-1", Name = "电机", Price = 30, Cost = 10, Currency = "USD" }, PlatformJson.Options, Ct);
        locked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(locked)).Should().Be("erp_product.currency_locked");

        var summary = await GetAsync<ErpSummaryDto>(client, "/api/erp/summary");
        summary.ReceivedThisMonth.Should().BeEquivalentTo([new ErpAmountDto("CNY", 420), new ErpAmountDto("USD", 2)]);
        summary.StockValue.Should().ContainSingle().Which.Currency.Should().Be("CNY");
    }

    [Fact]
    public async Task Returns_restock_at_shipping_cost_and_supplier_returns_only_use_available_stock()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        await EventWorkflowAsync(client, "erp.sales_order_returned", "退货 {{ trigger.document.number }} {{ trigger.document.amount }} {{ trigger.document.note }}");
        var product = await ProductAsync(client, "R-1", "水泵", price: 50);
        await PostAsync<ErpStockLevelDto>(client, "/api/erp/stock/adjust", new AdjustErpStockRequest { ProductId = product.Id, Quantity = 10, UnitCost = 20, Note = "期初" });

        var order = await PostAsync<ErpSalesOrderDto>(client, "/api/erp/sales-orders?confirm=true", new SaveErpSalesOrderRequest { Lines = [Line(product.Id, 6)] });
        var early = await client.PostAsJsonAsync($"/api/erp/sales-orders/{order.Id}/returns", new ErpReturnRequest { Lines = [Quantity(product.Id, 1)] }, PlatformJson.Options, Ct);
        (await CodeAsync(early)).Should().Be("erp_sales_order.status_changed");
        var shipped = await PostAsync<ErpSalesOrderDto>(client, $"/api/erp/sales-orders/{order.Id}/ship", new { });
        (shipped.ShippedAmount, shipped.CostOfGoods, shipped.GrossProfit).Should().Be((300m, 120m, 180m));

        // 之后进价变了：退回的货仍按发货时的成本入库，毛利按发货时的口径冲回。
        await PostAsync<ErpStockLevelDto>(client, "/api/erp/stock/adjust", new AdjustErpStockRequest { ProductId = product.Id, Quantity = 10, UnitCost = 30, Note = "补货" });
        var returned = await PostAsync<ErpSalesOrderDto>(client, $"/api/erp/sales-orders/{order.Id}/returns", new ErpReturnRequest
        {
            Lines = [Quantity(product.Id, 2)],
            Note = "包装破损",
        });
        returned.Status.Should().Be(ErpSalesOrderStatus.Shipped);
        returned.Lines[0].Returned.Should().Be(2);
        (returned.ShippedAmount, returned.CostOfGoods, returned.GrossProfit).Should().Be((200m, 80m, 120m));
        var level = await LevelAsync(client, product.Id);
        level.OnHand.Should().Be(16);
        level.AverageCost.Should().Be(26.25m);
        var restock = (await ProductDetailAsync(client, product.Id)).Movements[0];
        restock.Should().Match<ErpStockMovementDto>(m => m.Kind == ErpStockMovementKind.SalesReturn && m.Quantity == 2 && m.UnitCost == 20 && m.DocumentNumber == "SR-0001");

        var tooMany = await client.PostAsJsonAsync($"/api/erp/sales-orders/{order.Id}/returns", new ErpReturnRequest { Lines = [Quantity(product.Id, 5)] }, PlatformJson.Options, Ct);
        tooMany.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsAsync(tooMany)).Should().Contain("「水泵」最多还能退 4 件。");
        (await client.PostAsJsonAsync($"/api/erp/sales-orders/{order.Id}/returns", new ErpReturnRequest { Lines = [] }, PlatformJson.Options, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var task = await EventuallyAsync(() => GetAsync<PagedResult<WorkItemDto>>(client, "/api/work-items"), r => r.Total == 1);
        task.Items[0].Title.Should().Be("退货 SR-0001 100 包装破损");

        // 退给供应商只能动用可用数量：已预留给销售订单的不能退。
        var supplier = await PostAsync<CrmCompanyDto>(client, "/api/crm/companies", new SaveCrmCompanyRequest { Name = "华东五金" });
        var po = await PlacedOrderAsync(client, supplier.Id, product.Id, 4, 30);
        await ReceiveAllAsync(client, po);
        await PostAsync<ErpSalesOrderDto>(client, "/api/erp/sales-orders?confirm=true", new SaveErpSalesOrderRequest { Lines = [Line(product.Id, 18)] });
        var blocked = await client.PostAsJsonAsync($"/api/erp/purchase-orders/{po.Id}/returns", new ErpReturnRequest { Lines = [Quantity(product.Id, 3)], Note = "质量问题" }, PlatformJson.Options, Ct);
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(blocked)).Should().Be("erp.insufficient_stock");
        var supplierReturn = await PostAsync<ErpPurchaseOrderDto>(client, $"/api/erp/purchase-orders/{po.Id}/returns", new ErpReturnRequest
        {
            Lines = [Quantity(product.Id, 2)],
            Note = "质量问题",
        });
        supplierReturn.Lines[0].Returned.Should().Be(2);
        supplierReturn.ReceivedAmount.Should().Be(60);
        var documents = await GetAsync<PagedResult<ErpStockDocumentDto>>(client, $"/api/erp/stock-documents?purchaseOrderId={po.Id}&kind=purchase_return");
        documents.Items.Should().ContainSingle().Which.Should().Match<ErpStockDocumentDto>(d => d.Number == "PR-0001" && d.Amount == 60 && d.CompanyName == "华东五金");
        (await ProductDetailAsync(client, product.Id)).Product.Should().Match<ErpProductDto>(p => p.OnHand == 18 && p.Reserved == 18);

        // 概况、经营分析、动作口径一致：发货扣除退货，毛利扣除冲回的成本。
        var summary = await GetAsync<ErpSummaryDto>(client, "/api/erp/summary");
        summary.ShippedThisMonth.Should().BeEquivalentTo([new ErpAmountDto("CNY", 200)]);
        summary.GrossProfitThisMonth.Should().BeEquivalentTo([new ErpAmountDto("CNY", 120)]);
        summary.ReceivedThisMonth.Should().BeEquivalentTo([new ErpAmountDto("CNY", 60)]);
        var profit = await InvokeAsync(client, "analytics.query", new { metric = "erp.gross_profit", range = "this_month", breakdown = "product" });
        profit.GetProperty("total").GetDecimal().Should().Be(120);
        profit.GetProperty("groups")[0].GetProperty("label").GetString().Should().Be("R-1 水泵");
        (await InvokeAsync(client, "analytics.query", new { metric = "erp.sales_shipped", range = "this_month" })).GetProperty("total").GetDecimal().Should().Be(200);
        (await InvokeAsync(client, "analytics.query", new { metric = "erp.purchases_received", range = "this_month", breakdown = "supplier" })).GetProperty("total").GetDecimal().Should().Be(60);
    }

    [Fact]
    public async Task Shipping_receiving_and_returns_follow_roles_and_tenants()
    {
        var owner = await RegisterAsync();
        using var ownerClient = Client(owner.AccessToken);
        var roles = await GetAsync<List<RoleDto>>(ownerClient, "/api/roles");
        using var viewerClient = Client(await AddMemberAsync(ownerClient, "viewer@example.com", roles.Single(r => r.Key == "viewer").Id));
        using var memberClient = Client(await AddMemberAsync(ownerClient, "member@example.com", roles.Single(r => r.Key == "member").Id));
        var product = await ProductAsync(ownerClient, "W-1", "螺丝", price: 10, cost: 4);
        await AdjustAsync(ownerClient, product.Id, 10);
        var order = await PostAsync<ErpSalesOrderDto>(ownerClient, "/api/erp/sales-orders?confirm=true", new SaveErpSalesOrderRequest { Lines = [Line(product.Id, 3)] });
        var supplier = await PostAsync<CrmCompanyDto>(ownerClient, "/api/crm/companies", new SaveCrmCompanyRequest { Name = "华东五金" });
        var po = await PlacedOrderAsync(ownerClient, supplier.Id, product.Id, 5, 4);

        // 只读成员能看单据，不能发货、退货；普通成员能处理销售订单，不能收货。
        (await viewerClient.PostAsJsonAsync($"/api/erp/sales-orders/{order.Id}/ship", new { }, PlatformJson.Options, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await PostAsync<ErpSalesOrderDto>(memberClient, $"/api/erp/sales-orders/{order.Id}/ship", new { });
        (await viewerClient.PostAsJsonAsync($"/api/erp/sales-orders/{order.Id}/returns", new ErpReturnRequest { Lines = [Quantity(product.Id, 1)] }, PlatformJson.Options, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await PostAsync<ErpSalesOrderDto>(memberClient, $"/api/erp/sales-orders/{order.Id}/returns", new ErpReturnRequest { Lines = [Quantity(product.Id, 1)] });
        (await memberClient.PostAsJsonAsync($"/api/erp/purchase-orders/{po.Id}/receive", new { }, PlatformJson.Options, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var visible = await GetAsync<PagedResult<ErpStockDocumentDto>>(viewerClient, $"/api/erp/stock-documents?productId={product.Id}");
        visible.Items.Select(d => d.Kind).Should().Equal(ErpStockDocumentKind.SalesReturn, ErpStockDocumentKind.Shipment);
        visible.Items[1].CreatedBy.Name.Should().Be("member");

        // 其他空间看不到也动不了。
        var other = await RegisterAsync("other@example.com", "别的空间");
        using var otherClient = Client(other.AccessToken);
        (await otherClient.GetAsync(new Uri($"/api/erp/stock-documents/{visible.Items[0].Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync<PagedResult<ErpStockDocumentDto>>(otherClient, "/api/erp/stock-documents")).Items.Should().BeEmpty();
        (await otherClient.PostAsJsonAsync($"/api/erp/purchase-orders/{po.Id}/receive", new { }, PlatformJson.Options, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await PostAsync<ErpProductDto>(otherClient, "/api/erp/products", new SaveErpProductRequest { Sku = "W-1", Name = "螺丝" })).AverageCost.Should().BeNull();

        // 单据号按空间各自编号。
        var otherProduct = await ProductAsync(otherClient, "S-1", "服务", price: 5);
        await AdjustAsync(otherClient, otherProduct.Id, 1);
        var otherOrder = await PostAsync<ErpSalesOrderDto>(otherClient, "/api/erp/sales-orders?confirm=true", new SaveErpSalesOrderRequest { Lines = [Line(otherProduct.Id, 1)] });
        await PostAsync<ErpSalesOrderDto>(otherClient, $"/api/erp/sales-orders/{otherOrder.Id}/ship", new { });
        (await GetAsync<PagedResult<ErpStockDocumentDto>>(otherClient, "/api/erp/stock-documents")).Items.Should().ContainSingle().Which.Number.Should().Be("SH-0001");
    }

    [Fact]
    public async Task Actions_ship_receive_close_and_return_by_codes()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var company = await PostAsync<CrmCompanyDto>(client, "/api/crm/companies", new SaveCrmCompanyRequest { Name = "星火科技" });
        await InvokeAsync(client, "erp.create_product", new { sku = "W-1", name = "螺丝", price = 2, cost = 1 });
        (await InvokeAsync(client, "erp.adjust_stock", new { sku = "W-1", quantity = 50, unitCost = 1.5, note = "期初" })).GetProperty("averageCost").GetDecimal().Should().Be(1.5m);

        var order = await InvokeAsync(client, "erp.create_sales_order", new { companyId = company.Id, lines = new[] { new { sku = "W-1", quantity = 30 } }, confirm = true });
        var number = order.GetProperty("number").GetString()!;
        var partial = await InvokeAsync(client, "erp.ship_sales_order", new { order = number, lines = new[] { new { sku = "w-1", quantity = 10 } }, note = "顺丰 SF1001" });
        partial.GetProperty("status").GetString().Should().Be("partially_shipped");
        partial.GetProperty("grossProfit").GetDecimal().Should().Be(5);
        (await InvokeAsync(client, "erp.list_sales_orders", new { status = "partially_shipped" })).GetArrayLength().Should().Be(1);
        (await InvokeAsync(client, "erp.list_sales_orders", new { pending = true })).GetArrayLength().Should().Be(1);
        (await InvokeAsync(client, "erp.close_sales_order", new { order = number })).GetProperty("status").GetString().Should().Be("shipped");
        var returned = await InvokeAsync(client, "erp.return_sales_order", new { order = number, lines = new[] { new { sku = "W-1", quantity = 3 } }, note = "客户多订了" });
        returned.GetProperty("lines")[0].GetProperty("returned").GetDecimal().Should().Be(3);
        (await InvokeAsync(client, "erp.get_product_stock", new { sku = "W-1" })).GetProperty("product").GetProperty("onHand").GetDecimal().Should().Be(43);

        var po = (await InvokeAsync(client, "erp.create_purchase_order", new { supplierId = company.Id, lines = new[] { new { sku = "W-1", quantity = 20, unitPrice = 1.2 } } }))
            .GetProperty("number").GetString()!;
        await InvokeAsync(client, "erp.place_purchase_order", new { order = po });
        (await InvokeAsync(client, "erp.receive_purchase_order", new { order = po, lines = new[] { new { sku = "W-1", quantity = 5 } } }))
            .GetProperty("status").GetString().Should().Be("partially_received");
        (await InvokeAsync(client, "erp.close_purchase_order", new { order = po })).GetProperty("status").GetString().Should().Be("received");
        (await InvokeAsync(client, "erp.return_purchase_order", new { order = po, lines = new[] { new { sku = "W-1", quantity = 1 } }, warehouse = "默认仓库", note = "坏了" }))
            .GetProperty("receivedAmount").GetDecimal().Should().Be(4.8m);

        var tooMany = await PostAsync<InvokeActionResponse>(client, "/api/actions/erp.return_purchase_order/invoke", new { order = po, lines = new[] { new { sku = "W-1", quantity = 9 } }, note = "x" });
        tooMany.Status.Should().Be("invalid_input");
        tooMany.Error.Should().Contain("最多还能退 4 件");

        var actions = await GetAsync<List<ActionDescriptorDto>>(client, "/api/actions");
        actions.Where(a => a.Key is "erp.close_sales_order" or "erp.return_sales_order" or "erp.close_purchase_order" or "erp.return_purchase_order")
            .Should().HaveCount(4).And.OnlyContain(a => a.RequiresApprovalForAgents);
        actions.Single(a => a.Key == "erp.receive_purchase_order").RequiresApprovalForAgents.Should().BeFalse();
    }

    [Fact]
    public async Task Closing_a_partially_received_purchase_order_stops_waiting_for_the_rest()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var product = await ProductAsync(client, "M-1", "电机", price: 30, cost: 10);
        var supplier = await PostAsync<CrmCompanyDto>(client, "/api/crm/companies", new SaveCrmCompanyRequest { Name = "华东五金" });
        var po = await PlacedOrderAsync(client, supplier.Id, product.Id, 10, 12);
        var close = $"/api/erp/purchase-orders/{po.Id}/close";

        // 一件都没到的订单不能结束收货（不要了应当取消）。
        var early = await client.PostAsJsonAsync(close, new { }, PlatformJson.Options, Ct);
        early.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(early)).Should().Be("erp_purchase_order.status_changed");
        await PostAsync<ErpPurchaseOrderDto>(client, $"/api/erp/purchase-orders/{po.Id}/receive", new ErpDeliveryRequest { Lines = [Quantity(product.Id, 4)] });

        // 普通成员没有采购权限；别的空间找不到这张订单。
        var member = (await GetAsync<List<RoleDto>>(client, "/api/roles")).Single(r => r.Key == "member");
        using var memberClient = Client(await AddMemberAsync(client, "member@example.com", member.Id));
        (await memberClient.PostAsJsonAsync(close, new { }, PlatformJson.Options, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var other = await RegisterAsync("other@example.com", "别的空间");
        using var otherClient = Client(other.AccessToken);
        (await otherClient.PostAsJsonAsync(close, new { }, PlatformJson.Options, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var closed = await PostAsync<ErpPurchaseOrderDto>(client, close, new { });

        closed.Status.Should().Be(ErpPurchaseOrderStatus.Received);
        closed.ReceivedAt.Should().NotBeNull();
        closed.Lines.Should().ContainSingle().Which.Should().Match<ErpOrderLineDto>(l => l.Quantity == 10 && l.Delivered == 4);
        closed.ReceivedAmount.Should().Be(48);
        (await GetAsync<PagedResult<ErpPurchaseOrderDto>>(client, "/api/erp/purchase-orders?pending=true")).Items.Should().BeEmpty();
        (await LevelAsync(client, product.Id)).OnHand.Should().Be(4);

        // 结束后不能再收货，也不能重复结束；已收到的货仍然可以退给供应商。
        var receive = await client.PostAsJsonAsync($"/api/erp/purchase-orders/{po.Id}/receive", new { }, PlatformJson.Options, Ct);
        (await CodeAsync(receive)).Should().Be("erp_purchase_order.status_changed");
        var again = await client.PostAsJsonAsync(close, new { }, PlatformJson.Options, Ct);
        (await CodeAsync(again)).Should().Be("erp_purchase_order.status_changed");
        var returned = await PostAsync<ErpPurchaseOrderDto>(client, $"/api/erp/purchase-orders/{po.Id}/returns", new ErpReturnRequest { Lines = [Quantity(product.Id, 1)], Note = "坏了" });
        returned.Lines[0].Returned.Should().Be(1);
        (await LevelAsync(client, product.Id)).OnHand.Should().Be(3);
    }

    private static ErpOrderLineRequest Line(Guid productId, decimal quantity) => new() { ProductId = productId, Quantity = quantity };

    private static ErpQuantityLineRequest Quantity(Guid productId, decimal quantity) => new() { ProductId = productId, Quantity = quantity };

    private static async Task<ErpProductDto> ProductAsync(HttpClient client, string sku, string name, decimal? price = null, decimal? cost = null)
        => await PostAsync<ErpProductDto>(client, "/api/erp/products", new SaveErpProductRequest { Sku = sku, Name = name, Price = price, Cost = cost });

    private static Task<ErpStockLevelDto> AdjustAsync(HttpClient client, Guid productId, decimal quantity)
        => PostAsync<ErpStockLevelDto>(client, "/api/erp/stock/adjust", new AdjustErpStockRequest { ProductId = productId, Quantity = quantity, Note = "调整" });

    private static Task<ErpProductDetailDto> ProductDetailAsync(HttpClient client, Guid productId)
        => GetAsync<ErpProductDetailDto>(client, $"/api/erp/products/{productId}");

    private static async Task<ErpStockLevelDto> LevelAsync(HttpClient client, Guid productId)
        => (await ProductDetailAsync(client, productId)).Levels.Single(l => l.WarehouseName == "默认仓库");

    private static async Task<ErpPurchaseOrderDto> PlacedOrderAsync(HttpClient client, Guid supplierId, Guid productId, decimal quantity, decimal unitPrice)
    {
        var order = await PostAsync<ErpPurchaseOrderDto>(client, "/api/erp/purchase-orders", new SaveErpPurchaseOrderRequest
        {
            SupplierId = supplierId,
            Lines = [new ErpOrderLineRequest { ProductId = productId, Quantity = quantity, UnitPrice = unitPrice }],
        });
        return await PostAsync<ErpPurchaseOrderDto>(client, $"/api/erp/purchase-orders/{order.Id}/place", new { });
    }

    private static Task<ErpPurchaseOrderDto> ReceiveAllAsync(HttpClient client, ErpPurchaseOrderDto order)
        => PostAsync<ErpPurchaseOrderDto>(client, $"/api/erp/purchase-orders/{order.Id}/receive", new { });

    private static Task<WorkflowDto> EventWorkflowAsync(HttpClient client, string eventType, string title)
        => PostAsync<WorkflowDto>(client, "/api/workflows", new SaveWorkflowRequest
        {
            Name = $"{eventType} 建任务",
            Trigger = new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Event, EventType = eventType },
            Steps =
            [
                new WorkflowStep
                {
                    Key = "task",
                    Name = "建任务",
                    Kind = WorkflowStepKind.Action,
                    ActionKey = "tasks.create",
                    Input = Json(JsonSerializer.Serialize(new { title }, PlatformJson.Options)),
                },
            ],
        });

    private static async Task<IReadOnlyList<string>> ErrorsAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return [.. problem.GetProperty("errors").EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(v => v.GetString()!))];
    }
}
