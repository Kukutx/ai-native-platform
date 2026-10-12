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

public sealed class ErpTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task New_tenant_has_a_default_warehouse_and_erp_depends_on_crm()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);

        var warehouses = await GetAsync<List<ErpWarehouseDto>>(client, "/api/erp/warehouses");
        warehouses.Should().ContainSingle(w => w.Name == "默认仓库" && w.IsDefault);
        auth.Session.EnabledModules.Should().Contain(["crm", "erp"]);
        var modules = await GetAsync<List<ModuleDto>>(client, "/api/modules");
        modules.Single(m => m.Key == "erp").DependsOn.Should().Equal("crm");

        // 依赖关系：先停进销存才能停客户管理；客户管理停用时不能启用进销存。
        var blocked = await client.PutAsJsonAsync("/api/modules/crm", new SetModuleEnabledRequest { Enabled = false }, PlatformJson.Options, Ct);
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(blocked)).Should().Be("module.required_by");
        (await PutAsync<ModuleDto>(client, "/api/modules/erp", new SetModuleEnabledRequest { Enabled = false })).Enabled.Should().BeFalse();
        await PutAsync<ModuleDto>(client, "/api/modules/crm", new SetModuleEnabledRequest { Enabled = false });
        var dependencyOff = await client.PutAsJsonAsync("/api/modules/erp", new SetModuleEnabledRequest { Enabled = true }, PlatformJson.Options, Ct);
        dependencyOff.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(dependencyOff)).Should().Be("module.dependency_disabled");
        (await client.GetAsync(new Uri("/api/erp/products", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await PutAsync<ModuleDto>(client, "/api/modules/crm", new SetModuleEnabledRequest { Enabled = true });
        (await PutAsync<ModuleDto>(client, "/api/modules/erp", new SetModuleEnabledRequest { Enabled = true })).Enabled.Should().BeTrue();
        (await client.GetAsync(new Uri("/api/erp/products", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Sales_orders_reserve_on_confirm_deduct_on_ship_and_release_on_cancel()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var widget = await ProductAsync(client, "w-100", "螺丝", price: 10, reorderPoint: 5);
        var service = await PostAsync<ErpProductDto>(client, "/api/erp/products", new SaveErpProductRequest { Sku = "SVC", Name = "安装服务", Unit = "次", Price = 100, TrackInventory = false });
        await AdjustAsync(client, widget.Id, 20, "期初");
        var company = await PostAsync<CrmCompanyDto>(client, "/api/crm/companies", new SaveCrmCompanyRequest { Name = "星火科技" });
        var deal = await PostAsync<CrmDealDto>(client, "/api/crm/deals", new SaveCrmDealRequest { Title = "年度采购", CompanyId = company.Id, Currency = "CNY" });

        // 从商机下单：客户和货币从商机带出，单价取产品售价。
        var order = await PostAsync<ErpSalesOrderDto>(client, "/api/erp/sales-orders", new SaveErpSalesOrderRequest
        {
            DealId = deal.Id,
            Lines = [Line(widget.Id, 8), Line(service.Id, 1)],
        });
        order.Number.Should().Be("SO-0001");
        order.Status.Should().Be(ErpSalesOrderStatus.Draft);
        order.CompanyName.Should().Be("星火科技");
        order.DealTitle.Should().Be("年度采购");
        order.WarehouseName.Should().Be("默认仓库");
        order.Total.Should().Be(180);
        order.Lines.Select(l => (l.Sku, l.Amount)).Should().Equal(("W-100", 80m), ("SVC", 100m));
        order.Owner!.Id.Should().Be(auth.Session.User.Id);

        // 商机属于别的客户时不能挂在这张订单上。
        var stranger = await PostAsync<CrmCompanyDto>(client, "/api/crm/companies", new SaveCrmCompanyRequest { Name = "远山集团" });
        var mismatched = await client.PostAsJsonAsync(
            "/api/erp/sales-orders", new SaveErpSalesOrderRequest { DealId = deal.Id, CompanyId = stranger.Id, Lines = [Line(widget.Id, 1)] }, PlatformJson.Options, Ct);
        mismatched.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var confirmed = await PostAsync<ErpSalesOrderDto>(client, $"/api/erp/sales-orders/{order.Id}/confirm", new { });
        confirmed.Status.Should().Be(ErpSalesOrderStatus.Confirmed);
        var stock = await ProductDetailAsync(client, widget.Id);
        stock.Product.Should().Match<ErpProductDto>(p => p.OnHand == 20 && p.Reserved == 8 && p.Available == 12);
        (await ProductDetailAsync(client, service.Id)).Product.OnHand.Should().Be(0);

        // 可用数量不够时整单不确认，列出缺货明细。
        var second = await PostAsync<ErpSalesOrderDto>(client, "/api/erp/sales-orders", new SaveErpSalesOrderRequest { CompanyId = company.Id, Lines = [Line(widget.Id, 15)] });
        second.Number.Should().Be("SO-0002");
        var shortage = await client.PostAsJsonAsync($"/api/erp/sales-orders/{second.Id}/confirm", new { }, PlatformJson.Options, Ct);
        shortage.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await shortage.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("code").GetString().Should().Be("erp.insufficient_stock");
        problem.GetProperty("title").GetString().Should().Contain("W-100 螺丝 缺 3 件（可用 12）");
        (await GetAsync<ErpSalesOrderDto>(client, $"/api/erp/sales-orders/{second.Id}")).Status.Should().Be(ErpSalesOrderStatus.Draft);

        var shipped = await PostAsync<ErpSalesOrderDto>(client, $"/api/erp/sales-orders/{order.Id}/ship", new { });
        shipped.Status.Should().Be(ErpSalesOrderStatus.Shipped);
        shipped.ShippedAt.Should().NotBeNull();
        stock = await ProductDetailAsync(client, widget.Id);
        stock.Product.Should().Match<ErpProductDto>(p => p.OnHand == 12 && p.Reserved == 0 && p.Available == 12);
        stock.Movements.Select(m => (m.Kind, m.Quantity, m.BalanceAfter)).Should().Equal(
            (ErpStockMovementKind.Sale, -8m, 12m),
            (ErpStockMovementKind.Adjustment, 20m, 20m));
        stock.Movements[0].SalesOrderNumber.Should().Be("SO-0001");
        stock.Movements[0].Actor.Id.Should().Be(auth.Session.User.Id);

        var again = await client.PostAsJsonAsync($"/api/erp/sales-orders/{order.Id}/ship", new { }, PlatformJson.Options, Ct);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(again)).Should().Be("erp_sales_order.status_changed");
        var cancelShipped = await client.PostAsJsonAsync($"/api/erp/sales-orders/{order.Id}/cancel", new { }, PlatformJson.Options, Ct);
        cancelShipped.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.DeleteAsync(new Uri($"/api/erp/sales-orders/{order.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // 草稿可以修改；带上读到的版本，过期的版本被拒绝。
        var edited = await PutAsync<ErpSalesOrderDto>(client, $"/api/erp/sales-orders/{second.Id}", new SaveErpSalesOrderRequest
        {
            CompanyId = company.Id,
            Lines = [new ErpOrderLineRequest { ProductId = widget.Id, Quantity = 10, UnitPrice = 9.5m }],
            Note = "老客户优惠",
            ExpectedUpdatedAt = second.UpdatedAt,
        });
        edited.Total.Should().Be(95);
        var stale = await client.PutAsJsonAsync($"/api/erp/sales-orders/{second.Id}", new SaveErpSalesOrderRequest
        {
            CompanyId = company.Id,
            Lines = [Line(widget.Id, 1)],
            ExpectedUpdatedAt = second.UpdatedAt,
        }, PlatformJson.Options, Ct);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(stale)).Should().Be("erp_sales_order.modified");

        // 建单时直接确认；取消已确认的订单释放预留。
        var direct = await PostAsync<ErpSalesOrderDto>(client, "/api/erp/sales-orders?confirm=true", new SaveErpSalesOrderRequest { CompanyId = company.Id, Lines = [Line(widget.Id, 5)] });
        direct.Status.Should().Be(ErpSalesOrderStatus.Confirmed);
        (await ProductDetailAsync(client, widget.Id)).Product.Reserved.Should().Be(5);
        var cancelled = await PostAsync<ErpSalesOrderDto>(client, $"/api/erp/sales-orders/{direct.Id}/cancel", new { });
        cancelled.Status.Should().Be(ErpSalesOrderStatus.Cancelled);
        (await ProductDetailAsync(client, widget.Id)).Product.Reserved.Should().Be(0);

        // 建单时确认但库存不够：整单不建，订单号也不占用。
        var refused = await client.PostAsJsonAsync("/api/erp/sales-orders?confirm=true", new SaveErpSalesOrderRequest { Lines = [Line(widget.Id, 100)] }, PlatformJson.Options, Ct);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await GetAsync<PagedResult<ErpSalesOrderDto>>(client, "/api/erp/sales-orders")).Total.Should().Be(3);
        (await client.DeleteAsync(new Uri($"/api/erp/sales-orders/{direct.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var byDeal = await GetAsync<PagedResult<ErpSalesOrderDto>>(client, $"/api/erp/sales-orders?dealId={deal.Id}");
        byDeal.Items.Should().ContainSingle(o => o.Id == order.Id);
        (await GetAsync<PagedResult<ErpSalesOrderDto>>(client, "/api/erp/sales-orders?search=星火&status=draft")).Items.Should().ContainSingle(o => o.Id == second.Id);
        var summary = await GetAsync<ErpSummaryDto>(client, "/api/erp/summary");
        summary.ShippedThisMonth.Should().BeEquivalentTo([new ErpAmountDto("CNY", 180)]);
        summary.DraftSalesOrders.Should().Be(1);
        summary.ToShipSalesOrders.Should().Be(0);
        summary.ActiveProducts.Should().Be(2);
    }

    [Fact]
    public async Task Concurrent_confirmations_never_oversell()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var widget = await ProductAsync(client, "W-1", "螺丝", price: 1);
        await AdjustAsync(client, widget.Id, 10, "期初");
        var orders = new List<ErpSalesOrderDto>();
        for (var i = 0; i < 4; i++)
        {
            orders.Add(await PostAsync<ErpSalesOrderDto>(client, "/api/erp/sales-orders", new SaveErpSalesOrderRequest { Lines = [Line(widget.Id, 4)] }));
        }

        var responses = await Task.WhenAll(orders.Select(o => client.PostAsJsonAsync($"/api/erp/sales-orders/{o.Id}/confirm", new { }, PlatformJson.Options, Ct)));

        responses.Count(r => r.IsSuccessStatusCode).Should().Be(2);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(2);
        var product = (await ProductDetailAsync(client, widget.Id)).Product;
        product.Reserved.Should().Be(8);
        product.Available.Should().Be(2);
    }

    [Fact]
    public async Task Switching_inventory_tracking_never_interleaves_with_confirmation()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var product = await PostAsync<ErpProductDto>(client, "/api/erp/products", new SaveErpProductRequest { Sku = "SVC", Name = "安装服务", Price = 1, TrackInventory = false });
        var orders = new List<ErpSalesOrderDto>();
        for (var i = 0; i < 4; i++)
        {
            orders.Add(await PostAsync<ErpSalesOrderDto>(client, "/api/erp/sales-orders", new SaveErpSalesOrderRequest { Lines = [Line(product.Id, 1)] }));
        }

        // 确认订单与"改为管库存"同时发生：先确认的按不管库存确认、切换被拒；先切换的按管库存预留（库存为 0，确认失败）。
        var confirms = orders.Select(o => client.PostAsJsonAsync($"/api/erp/sales-orders/{o.Id}/confirm", new { }, PlatformJson.Options, Ct));
        var toggle = client.PutAsJsonAsync($"/api/erp/products/{product.Id}", Save(product) with { TrackInventory = true }, PlatformJson.Options, Ct);
        var responses = await Task.WhenAll([.. confirms, toggle]);
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict);

        // 无论先后，已确认的订单都能正常取消：预留与库存始终一致。
        var confirmed = await GetAsync<PagedResult<ErpSalesOrderDto>>(client, "/api/erp/sales-orders?status=confirmed");
        foreach (var order in confirmed.Items)
        {
            (await PostAsync<ErpSalesOrderDto>(client, $"/api/erp/sales-orders/{order.Id}/cancel", new { })).Status.Should().Be(ErpSalesOrderStatus.Cancelled);
        }

        (await ProductDetailAsync(client, product.Id)).Product.Reserved.Should().Be(0);
    }

    [Fact]
    public async Task Stock_list_includes_products_that_never_moved()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var fresh = await ProductAsync(client, "NEW-1", "新品", reorderPoint: 5);
        var moved = await ProductAsync(client, "OLD-1", "老品");
        await AdjustAsync(client, moved.Id, 3, "期初");
        var second = await PostAsync<ErpWarehouseDto>(client, "/api/erp/warehouses", new SaveErpWarehouseRequest { Name = "二号仓" });

        // 不选仓库：从没动过库存的新品在默认仓库显示 0，并计入"需要补货"，与概况的计数一致。
        var all = await GetAsync<PagedResult<ErpStockLevelDto>>(client, "/api/erp/stock");
        all.Items.Select(l => (l.Sku, l.WarehouseName, l.OnHand)).Should().Equal(("NEW-1", "默认仓库", 0m), ("OLD-1", "默认仓库", 3m));
        var low = await GetAsync<PagedResult<ErpStockLevelDto>>(client, "/api/erp/stock?lowStockOnly=true");
        low.Items.Should().ContainSingle(l => l.ProductId == fresh.Id);
        (await GetAsync<ErpSummaryDto>(client, "/api/erp/summary")).LowStockProducts.Should().Be(1);

        // 选了仓库：列出全部管库存的产品在该仓库的数量。
        var inSecond = await GetAsync<PagedResult<ErpStockLevelDto>>(client, $"/api/erp/stock?warehouseId={second.Id}");
        inSecond.Items.Select(l => (l.Sku, l.WarehouseName, l.OnHand)).Should().Equal(("NEW-1", "二号仓", 0m), ("OLD-1", "二号仓", 0m));
        (await GetAsync<PagedResult<ErpStockLevelDto>>(client, $"/api/erp/stock?productId={moved.Id}")).Items.Should().ContainSingle(l => l.OnHand == 3);
    }

    [Fact]
    public async Task Amounts_prices_and_dates_are_validated_before_saving()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var widget = await ProductAsync(client, "W-1", "螺丝", price: 1);
        var supplier = await PostAsync<CrmCompanyDto>(client, "/api/crm/companies", new SaveCrmCompanyRequest { Name = "供应商" });

        // 单价、售价最多 4 位小数，与数据库精度一致。
        var fine = await client.PostAsJsonAsync("/api/erp/sales-orders", new SaveErpSalesOrderRequest
        {
            Lines = [new ErpOrderLineRequest { ProductId = widget.Id, Quantity = 100_000, UnitPrice = 0.000009m }],
        }, PlatformJson.Options, Ct);
        fine.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var price = await client.PostAsJsonAsync("/api/erp/products", new SaveErpProductRequest { Sku = "P-1", Name = "x", Price = 0.00001m, Cost = 0.00001m }, PlatformJson.Options, Ct);
        var priceErrors = (await price.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");
        priceErrors.TryGetProperty("price", out _).Should().BeTrue();
        priceErrors.TryGetProperty("cost", out _).Should().BeTrue();

        // 金额超出数据库能保存的范围时返回 400，而不是保存时报错。
        var huge = await client.PostAsJsonAsync("/api/erp/sales-orders", new SaveErpSalesOrderRequest
        {
            Lines = [new ErpOrderLineRequest { ProductId = widget.Id, Quantity = 999_999_999m, UnitPrice = 999_999_999_999m }],
        }, PlatformJson.Options, Ct);
        huge.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await huge.Content.ReadAsStringAsync(Ct)).Should().Contain("金额太大");
        var many = await client.PostAsJsonAsync("/api/erp/purchase-orders", new SaveErpPurchaseOrderRequest
        {
            SupplierId = supplier.Id,
            Lines = [.. Enumerable.Range(0, 3).Select(_ => new ErpOrderLineRequest { ProductId = widget.Id, Quantity = 999_999_999m, UnitPrice = 1m })],
        }, PlatformJson.Options, Ct);
        many.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await many.Content.ReadAsStringAsync(Ct)).Should().Contain("数量太大");

        // 极端的负数同样是 400。
        var minimum = await client.PostAsJsonAsync("/api/erp/stock/adjust", new AdjustErpStockRequest { ProductId = widget.Id, Quantity = decimal.MinValue, Note = "x" }, PlatformJson.Options, Ct);
        minimum.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 不填下单日期时按今天检查预计到货日期（智能体起草采购单就是这样）。
        var late = await PostAsync<InvokeActionResponse>(client, "/api/actions/erp.create_purchase_order/invoke", new
        {
            supplierId = supplier.Id,
            expectedDate = DateOnly.FromDateTime(DateTime.Now).AddDays(-2),
            lines = new[] { new { sku = "W-1", quantity = 1 } },
        });
        late.Status.Should().Be("invalid_input");
        late.Error.Should().Contain("预计到货日期");
    }

    [Fact]
    public async Task Action_references_resolve_under_the_action_permission()
    {
        var owner = await RegisterAsync();
        using var ownerClient = Client(owner.AccessToken);
        var widget = await ProductAsync(ownerClient, "W-1", "螺丝", price: 2);
        await PostAsync<ErpWarehouseDto>(ownerClient, "/api/erp/warehouses", new SaveErpWarehouseRequest { Name = "二号仓" });
        var storekeeper = await PostAsync<RoleDto>(ownerClient, "/api/roles", new SaveRoleRequest { Key = "storekeeper", Name = "库管", Features = ["erp.inventory"] });
        var seller = await PostAsync<RoleDto>(ownerClient, "/api/roles", new SaveRoleRequest { Key = "seller", Name = "销售", Features = ["erp.sales"] });
        using var storekeeperClient = Client(await AddMemberAsync(ownerClient, "store@example.com", storekeeper.Id));
        using var sellerClient = Client(await AddMemberAsync(ownerClient, "seller@example.com", seller.Id));

        // 只有库存权限、没有查看权限：用编码和仓库名称调整库存，与用 ID 一样能执行。
        var adjusted = await InvokeAsync(storekeeperClient, "erp.adjust_stock", new { sku = "w-1", warehouse = "二号仓", quantity = 5, note = "期初" });
        adjusted.GetProperty("warehouseName").GetString().Should().Be("二号仓");
        adjusted.GetProperty("onHand").GetDecimal().Should().Be(5);

        // 只有销售权限：用编码下单、用订单号确认。
        var order = await InvokeAsync(sellerClient, "erp.create_sales_order", new { warehouse = "二号仓", lines = new[] { new { sku = "W-1", quantity = 2 } } });
        var number = order.GetProperty("number").GetString()!;
        (await InvokeAsync(sellerClient, "erp.confirm_sales_order", new { order = number })).GetProperty("status").GetString().Should().Be("confirmed");
        var missing = await PostAsync<InvokeActionResponse>(sellerClient, "/api/actions/erp.confirm_sales_order/invoke", new { order = "SO-9999" });
        missing.Status.Should().NotBe("succeeded");
        (await ProductDetailAsync(ownerClient, widget.Id)).Product.Reserved.Should().Be(2);
    }

    [Fact]
    public async Task Purchase_receipts_add_stock_and_low_stock_triggers_a_workflow_once()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        await PostAsync<WorkflowDto>(client, "/api/workflows", new SaveWorkflowRequest
        {
            Name = "低库存提醒补货",
            Trigger = new WorkflowTriggerDto { Kind = WorkflowTriggerKind.Event, EventType = "erp.stock_low" },
            Steps =
            [
                new WorkflowStep
                {
                    Key = "remind",
                    Name = "建补货任务",
                    Kind = WorkflowStepKind.Action,
                    ActionKey = "tasks.create",
                    Input = Json("""{ "title": "补货 {{ trigger.product.sku }} {{ trigger.product.name }}", "description": "可用 {{ trigger.product.available }}，提醒线 {{ trigger.product.reorderPoint }}" }"""),
                },
            ],
        });
        var widget = await ProductAsync(client, "W-1", "螺丝", price: 10, cost: 6, reorderPoint: 5);
        await AdjustAsync(client, widget.Id, 10, "期初");

        await PostAsync<ErpSalesOrderDto>(client, "/api/erp/sales-orders?confirm=true", new SaveErpSalesOrderRequest { Lines = [Line(widget.Id, 6)] });
        await PostAsync<ErpSalesOrderDto>(client, "/api/erp/sales-orders?confirm=true", new SaveErpSalesOrderRequest { Lines = [Line(widget.Id, 1)] });

        var runs = await EventuallyAsync(
            () => GetAsync<PagedResult<WorkflowRunSummaryDto>>(client, "/api/workflows/runs"),
            r => r.Items.Count == 1 && r.Items[0].Status == WorkflowRunStatus.Succeeded);
        runs.Items[0].EventType.Should().Be("erp.stock_low");
        var task = (await GetAsync<PagedResult<WorkItemDto>>(client, "/api/work-items")).Items.Should().ContainSingle().Subject;
        task.Title.Should().Be("补货 W-1 螺丝");
        task.Description.Should().Be("可用 4，提醒线 5");
        (await GetAsync<PagedResult<ErpProductDto>>(client, "/api/erp/products?lowStockOnly=true")).Items.Should().ContainSingle(p => p.Id == widget.Id && p.LowStock);

        var supplier = await PostAsync<CrmCompanyDto>(client, "/api/crm/companies", new SaveCrmCompanyRequest { Name = "华东五金" });
        var po = await PostAsync<ErpPurchaseOrderDto>(client, "/api/erp/purchase-orders", new SaveErpPurchaseOrderRequest
        {
            SupplierId = supplier.Id,
            Lines = [Line(widget.Id, 20)],
            ExpectedDate = DateOnly.FromDateTime(DateTime.Today.AddDays(7)),
        });
        po.Number.Should().Be("PO-0001");
        po.Total.Should().Be(120);
        po.SupplierName.Should().Be("华东五金");

        var notPlaced = await client.PostAsJsonAsync($"/api/erp/purchase-orders/{po.Id}/receive", new { }, PlatformJson.Options, Ct);
        notPlaced.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(notPlaced)).Should().Be("erp_purchase_order.status_changed");
        (await PostAsync<ErpPurchaseOrderDto>(client, $"/api/erp/purchase-orders/{po.Id}/place", new { })).Status.Should().Be(ErpPurchaseOrderStatus.Ordered);
        var locked = await client.PutAsJsonAsync($"/api/erp/purchase-orders/{po.Id}", new SaveErpPurchaseOrderRequest { SupplierId = supplier.Id, Lines = [Line(widget.Id, 1)] }, PlatformJson.Options, Ct);
        locked.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var received = await PostAsync<ErpPurchaseOrderDto>(client, $"/api/erp/purchase-orders/{po.Id}/receive", new { });
        received.Status.Should().Be(ErpPurchaseOrderStatus.Received);
        var stock = await ProductDetailAsync(client, widget.Id);
        stock.Product.Should().Match<ErpProductDto>(p => p.OnHand == 30 && p.Reserved == 7 && !p.LowStock);
        stock.Movements[0].Should().Match<ErpStockMovementDto>(m => m.Kind == ErpStockMovementKind.Purchase && m.Quantity == 20 && m.PurchaseOrderNumber == "PO-0001");
        (await client.PostAsJsonAsync($"/api/erp/purchase-orders/{po.Id}/receive", new { }, PlatformJson.Options, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await GetAsync<ErpSummaryDto>(client, "/api/erp/summary")).ReceivedThisMonth.Should().BeEquivalentTo([new ErpAmountDto("CNY", 120)]);

        await WaitForIdleAsync();
        (await GetAsync<PagedResult<WorkflowRunSummaryDto>>(client, "/api/workflows/runs")).Total.Should().Be(1);
    }

    [Fact]
    public async Task Adjustments_counts_and_transfers_keep_a_ledger_and_protect_warehouses()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var widget = await ProductAsync(client, "W-1", "螺丝");
        var main = (await GetAsync<List<ErpWarehouseDto>>(client, "/api/erp/warehouses")).Single();
        var second = await PostAsync<ErpWarehouseDto>(client, "/api/erp/warehouses", new SaveErpWarehouseRequest { Name = "二号仓", Address = "上海" });
        var duplicate = await client.PostAsJsonAsync("/api/erp/warehouses", new SaveErpWarehouseRequest { Name = "二号仓" }, PlatformJson.Options, Ct);
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);

        await AdjustAsync(client, widget.Id, 10, "期初");
        var levels = await PostAsync<List<ErpStockLevelDto>>(client, "/api/erp/stock/transfer", new TransferErpStockRequest
        {
            ProductId = widget.Id,
            FromWarehouseId = main.Id,
            ToWarehouseId = second.Id,
            Quantity = 4,
        });
        levels.Select(l => (l.WarehouseName, l.OnHand)).Should().Equal(("默认仓库", 6m), ("二号仓", 4m));
        var tooMuch = await client.PostAsJsonAsync("/api/erp/stock/transfer", new TransferErpStockRequest
        {
            ProductId = widget.Id,
            FromWarehouseId = main.Id,
            ToWarehouseId = second.Id,
            Quantity = 7,
        }, PlatformJson.Options, Ct);
        tooMuch.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(tooMuch)).Should().Be("erp.insufficient_stock");

        // 盘点：按实际数量调整，自动写明前后数量。
        var counted = await PostAsync<ErpStockLevelDto>(client, "/api/erp/stock/adjust", new AdjustErpStockRequest { ProductId = widget.Id, CountedQuantity = 5 });
        counted.OnHand.Should().Be(5);
        var movements = await GetAsync<PagedResult<ErpStockMovementDto>>(client, $"/api/erp/stock/movements?productId={widget.Id}");
        movements.Items.Select(m => (m.Kind, m.Quantity, m.WarehouseName)).Should().Equal(
            (ErpStockMovementKind.Adjustment, -1m, "默认仓库"),
            (ErpStockMovementKind.TransferIn, 4m, "二号仓"),
            (ErpStockMovementKind.TransferOut, -4m, "默认仓库"),
            (ErpStockMovementKind.Adjustment, 10m, "默认仓库"));
        movements.Items[0].Note.Should().Be("盘点：6 → 5");

        var negative = await client.PostAsJsonAsync("/api/erp/stock/adjust", new AdjustErpStockRequest { ProductId = widget.Id, Quantity = -6, Note = "报损" }, PlatformJson.Options, Ct);
        negative.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var both = await client.PostAsJsonAsync("/api/erp/stock/adjust", new AdjustErpStockRequest { ProductId = widget.Id, Quantity = 1, CountedQuantity = 2 }, PlatformJson.Options, Ct);
        both.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var service = await PostAsync<ErpProductDto>(client, "/api/erp/products", new SaveErpProductRequest { Sku = "SVC", Name = "安装服务", TrackInventory = false });
        var untracked = await client.PostAsJsonAsync("/api/erp/stock/adjust", new AdjustErpStockRequest { ProductId = service.Id, Quantity = 1 }, PlatformJson.Options, Ct);
        (await CodeAsync(untracked)).Should().Be("erp_product.untracked");

        var stockLevels = await GetAsync<PagedResult<ErpStockLevelDto>>(client, $"/api/erp/stock?warehouseId={second.Id}");
        stockLevels.Items.Should().ContainSingle(l => l.ProductId == widget.Id && l.OnHand == 4);

        // 有库存、默认的仓库不能删；把二号仓设为默认后，原默认仓库自动取消默认。
        (await CodeAsync(await client.DeleteAsync(new Uri($"/api/erp/warehouses/{second.Id}", UriKind.Relative), Ct))).Should().Be("erp_warehouse.has_stock");
        (await CodeAsync(await client.DeleteAsync(new Uri($"/api/erp/warehouses/{main.Id}", UriKind.Relative), Ct))).Should().Be("erp_warehouse.default");
        await PutAsync<ErpWarehouseDto>(client, $"/api/erp/warehouses/{second.Id}", new SaveErpWarehouseRequest { Name = "二号仓", Address = "上海", IsDefault = true });
        var after = await GetAsync<List<ErpWarehouseDto>>(client, "/api/erp/warehouses");
        after.Select(w => (w.Name, w.IsDefault)).Should().Equal(("二号仓", true), ("默认仓库", false));
        var unsetDefault = await client.PutAsJsonAsync($"/api/erp/warehouses/{second.Id}", new SaveErpWarehouseRequest { Name = "二号仓" }, PlatformJson.Options, Ct);
        (await CodeAsync(unsetDefault)).Should().Be("erp_warehouse.default_required");
        var empty = await PostAsync<ErpWarehouseDto>(client, "/api/erp/warehouses", new SaveErpWarehouseRequest { Name = "临时仓" });
        (await client.DeleteAsync(new Uri($"/api/erp/warehouses/{empty.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Products_have_unique_codes_validation_and_safe_deletes()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var product = await ProductAsync(client, " ab-1 ", "不锈钢螺丝 M6", price: 0.5m);
        product.Sku.Should().Be("AB-1");
        product.Currency.Should().Be("CNY");

        var duplicate = await client.PostAsJsonAsync("/api/erp/products", new SaveErpProductRequest { Sku = "Ab-1", Name = "重复" }, PlatformJson.Options, Ct);
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(duplicate)).Should().Be("erp_product.duplicate_sku");

        var invalid = await client.PostAsJsonAsync("/api/erp/products", new SaveErpProductRequest { Sku = "A B", Name = " ", Price = -1, Currency = "人民币", ReorderPoint = 0.00001m }, PlatformJson.Options, Ct);
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");
        foreach (var field in new[] { "sku", "name", "price", "currency", "reorderPoint" })
        {
            errors.TryGetProperty(field, out _).Should().BeTrue(field);
        }

        (await GetAsync<PagedResult<ErpProductDto>>(client, "/api/erp/products?search=m6")).Items.Should().ContainSingle();
        (await GetAsync<PagedResult<ErpProductDto>>(client, "/api/erp/products?search=b-1")).Items.Should().ContainSingle();

        var updated = await PutAsync<ErpProductDto>(client, $"/api/erp/products/{product.Id}", Save(product) with { Name = "不锈钢螺丝 M6×20", ExpectedUpdatedAt = product.UpdatedAt });
        updated.Name.Should().Be("不锈钢螺丝 M6×20");
        var stale = await client.PutAsJsonAsync($"/api/erp/products/{product.Id}", Save(product) with { ExpectedUpdatedAt = product.UpdatedAt }, PlatformJson.Options, Ct);
        (await CodeAsync(stale)).Should().Be("erp_product.modified");

        await AdjustAsync(client, product.Id, 3, "期初");
        var untrack = await client.PutAsJsonAsync($"/api/erp/products/{product.Id}", Save(updated) with { TrackInventory = false }, PlatformJson.Options, Ct);
        (await CodeAsync(untrack)).Should().Be("erp_product.has_stock");
        var used = await client.DeleteAsync(new Uri($"/api/erp/products/{product.Id}", UriKind.Relative), Ct);
        (await CodeAsync(used)).Should().Be("erp_product.in_use");

        // 停用的产品不能再下单，但历史保留。
        await PutAsync<ErpProductDto>(client, $"/api/erp/products/{product.Id}", Save(updated) with { IsActive = false });
        var inactive = await client.PostAsJsonAsync("/api/erp/sales-orders", new SaveErpSalesOrderRequest { Lines = [Line(product.Id, 1)] }, PlatformJson.Options, Ct);
        inactive.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetAsync<PagedResult<ErpProductDto>>(client, "/api/erp/products")).Items.Should().BeEmpty();
        (await GetAsync<PagedResult<ErpProductDto>>(client, "/api/erp/products?includeInactive=true")).Items.Should().ContainSingle();

        // 没有价格、或价格货币与订单不同时要求填写单价。
        var usd = await PostAsync<ErpProductDto>(client, "/api/erp/products", new SaveErpProductRequest { Sku = "IMP-1", Name = "进口轴承", Price = 20, Currency = "USD", Category = "进口件" });
        var noPrice = await client.PostAsJsonAsync("/api/erp/sales-orders", new SaveErpSalesOrderRequest { Lines = [Line(usd.Id, 1)] }, PlatformJson.Options, Ct);
        noPrice.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await noPrice.Content.ReadAsStringAsync(Ct)).Should().Contain("USD");
        var priced = await PostAsync<ErpSalesOrderDto>(client, "/api/erp/sales-orders", new SaveErpSalesOrderRequest { Currency = "usd", Lines = [Line(usd.Id, 2)] });
        priced.Currency.Should().Be("USD");
        priced.Total.Should().Be(40);
        (await GetAsync<List<string>>(client, "/api/erp/products/categories")).Should().Equal("进口件");

        var fresh = await ProductAsync(client, "NEW-1", "新品");
        (await client.DeleteAsync(new Uri($"/api/erp/products/{fresh.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Roles_modules_and_tenants_limit_access()
    {
        var owner = await RegisterAsync();
        using var ownerClient = Client(owner.AccessToken);
        var roles = await GetAsync<List<RoleDto>>(ownerClient, "/api/roles");
        using var viewerClient = Client(await AddMemberAsync(ownerClient, "viewer@example.com", roles.Single(r => r.Key == "viewer").Id));
        using var memberClient = Client(await AddMemberAsync(ownerClient, "member@example.com", roles.Single(r => r.Key == "member").Id));
        var product = await ProductAsync(ownerClient, "W-1", "螺丝", price: 10);

        (await viewerClient.GetAsync(new Uri("/api/erp/products", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewerClient.PostAsJsonAsync("/api/erp/sales-orders", new SaveErpSalesOrderRequest { Lines = [Line(product.Id, 1)] }, PlatformJson.Options, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var memberOrder = await PostAsync<ErpSalesOrderDto>(memberClient, "/api/erp/sales-orders", new SaveErpSalesOrderRequest { Lines = [Line(product.Id, 1)] });
        memberOrder.Owner!.Name.Should().Be("member");
        (await memberClient.PostAsJsonAsync("/api/erp/products", new SaveErpProductRequest { Sku = "X", Name = "不允许" }, PlatformJson.Options, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await memberClient.PostAsJsonAsync("/api/erp/stock/adjust", new AdjustErpStockRequest { ProductId = product.Id, Quantity = 1 }, PlatformJson.Options, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var other = await RegisterAsync("other@example.com", "别的空间");
        using var otherClient = Client(other.AccessToken);
        (await otherClient.GetAsync(new Uri($"/api/erp/products/{product.Id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await otherClient.PostAsJsonAsync("/api/erp/sales-orders", new SaveErpSalesOrderRequest { Lines = [Line(product.Id, 1)] }, PlatformJson.Options, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await otherClient.PostAsJsonAsync($"/api/erp/sales-orders/{memberOrder.Id}/confirm", new { }, PlatformJson.Options, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync<PagedResult<ErpSalesOrderDto>>(otherClient, "/api/erp/sales-orders")).Items.Should().BeEmpty();

        // 订单号按空间各自编号。
        var otherProduct = await ProductAsync(otherClient, "W-1", "螺丝", price: 1);
        (await PostAsync<ErpSalesOrderDto>(otherClient, "/api/erp/sales-orders", new SaveErpSalesOrderRequest { Lines = [Line(otherProduct.Id, 1)] })).Number.Should().Be("SO-0001");

        await PutAsync<ModuleDto>(ownerClient, "/api/modules/erp", new SetModuleEnabledRequest { Enabled = false });
        (await memberClient.GetAsync(new Uri("/api/erp/sales-orders", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PostAsync<InvokeActionResponse>(memberClient, "/api/actions/erp.search_products/invoke", new { query = "螺丝" })).Status.Should().Be("not_found");
    }

    [Fact]
    public async Task Actions_let_agents_and_chat_work_with_codes_and_order_numbers()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var company = await PostAsync<CrmCompanyDto>(client, "/api/crm/companies", new SaveCrmCompanyRequest { Name = "星火科技" });
        var deal = await PostAsync<CrmDealDto>(client, "/api/crm/deals", new SaveCrmDealRequest { Title = "年度采购", CompanyId = company.Id });

        var created = await InvokeAsync(client, "erp.create_product", new { sku = "w-1", name = "螺丝", price = 2, cost = 1, reorderPoint = 10 });
        created.GetProperty("sku").GetString().Should().Be("W-1");
        var adjusted = await InvokeAsync(client, "erp.adjust_stock", new { sku = "W-1", quantity = 50, note = "期初" });
        adjusted.GetProperty("onHand").GetDecimal().Should().Be(50);

        var found = await InvokeAsync(client, "erp.search_products", new { query = "螺丝" });
        found[0].GetProperty("available").GetDecimal().Should().Be(50);
        var order = await InvokeAsync(client, "erp.create_sales_order", new { dealId = deal.Id, lines = new[] { new { sku = "w-1", quantity = 45 } }, confirm = true });
        order.GetProperty("status").GetString().Should().Be("confirmed");
        order.GetProperty("companyName").GetString().Should().Be("星火科技");
        var number = order.GetProperty("number").GetString()!;
        (await InvokeAsync(client, "erp.search_products", new { lowStockOnly = true })).GetArrayLength().Should().Be(1);

        var shipped = await InvokeAsync(client, "erp.ship_sales_order", new { order = number.ToLowerInvariant() });
        shipped.GetProperty("status").GetString().Should().Be("shipped");
        var stock = await InvokeAsync(client, "erp.get_product_stock", new { sku = "W-1" });
        stock.GetProperty("product").GetProperty("onHand").GetDecimal().Should().Be(5);
        stock.GetProperty("movements")[0].GetProperty("salesOrderNumber").GetString().Should().Be(number);

        var draft = await InvokeAsync(client, "erp.create_purchase_order", new { supplierId = company.Id, lines = new[] { new { sku = "W-1", quantity = 100 } } });
        draft.GetProperty("total").GetDecimal().Should().Be(100);
        var po = draft.GetProperty("number").GetString()!;
        (await InvokeAsync(client, "erp.place_purchase_order", new { order = po })).GetProperty("status").GetString().Should().Be("ordered");
        (await InvokeAsync(client, "erp.receive_purchase_order", new { order = po })).GetProperty("status").GetString().Should().Be("received");
        (await InvokeAsync(client, "erp.list_purchase_orders", new { status = "received" })).GetArrayLength().Should().Be(1);
        (await InvokeAsync(client, "erp.list_sales_orders", new { status = "shipped", dealId = deal.Id })).GetArrayLength().Should().Be(1);
        (await InvokeAsync(client, "erp.get_sales_order", new { order = number })).GetProperty("dealTitle").GetString().Should().Be("年度采购");
        var summary = await InvokeAsync(client, "erp.get_summary", new { });
        summary.GetProperty("shippedThisMonth")[0].GetProperty("amount").GetDecimal().Should().Be(90);

        var wrongSku = await PostAsync<InvokeActionResponse>(client, "/api/actions/erp.create_sales_order/invoke", new { lines = new[] { new { sku = "NOPE", quantity = 1 } } });
        wrongSku.Status.Should().Be("invalid_input");
        wrongSku.Error.Should().Contain("erp.search_products");
        var wrongWarehouse = await PostAsync<InvokeActionResponse>(client, "/api/actions/erp.adjust_stock/invoke", new { sku = "W-1", quantity = 1, warehouse = "火星仓", note = "x" });
        wrongWarehouse.Status.Should().Be("invalid_input");
        wrongWarehouse.Error.Should().Contain("默认仓库");
        var shortage = await PostAsync<InvokeActionResponse>(client, "/api/actions/erp.create_sales_order/invoke", new { lines = new[] { new { sku = "W-1", quantity = 1000 } }, confirm = true });
        shortage.Status.Should().Be("failed");
        shortage.Error.Should().Contain("缺");

        var actions = await GetAsync<List<ActionDescriptorDto>>(client, "/api/actions");
        actions.Single(a => a.Key == "erp.place_purchase_order").RequiresApprovalForAgents.Should().BeTrue();
        actions.Single(a => a.Key == "erp.adjust_stock").RequiresApprovalForAgents.Should().BeTrue();
        actions.Single(a => a.Key == "erp.ship_sales_order").RequiresApprovalForAgents.Should().BeFalse();
    }

    private static ErpOrderLineRequest Line(Guid productId, decimal quantity) => new() { ProductId = productId, Quantity = quantity };

    private static async Task<ErpProductDto> ProductAsync(HttpClient client, string sku, string name, decimal? price = null, decimal? cost = null, decimal? reorderPoint = null)
        => await PostAsync<ErpProductDto>(client, "/api/erp/products", new SaveErpProductRequest { Sku = sku, Name = name, Price = price, Cost = cost, ReorderPoint = reorderPoint });

    private static Task<ErpStockLevelDto> AdjustAsync(HttpClient client, Guid productId, decimal quantity, string note)
        => PostAsync<ErpStockLevelDto>(client, "/api/erp/stock/adjust", new AdjustErpStockRequest { ProductId = productId, Quantity = quantity, Note = note });

    private static Task<ErpProductDetailDto> ProductDetailAsync(HttpClient client, Guid productId)
        => GetAsync<ErpProductDetailDto>(client, $"/api/erp/products/{productId}");

    private static SaveErpProductRequest Save(ErpProductDto p) => new()
    {
        Sku = p.Sku,
        Name = p.Name,
        Unit = p.Unit,
        Category = p.Category,
        Description = p.Description,
        Price = p.Price,
        Cost = p.Cost,
        Currency = p.Currency,
        TrackInventory = p.TrackInventory,
        ReorderPoint = p.ReorderPoint,
        IsActive = p.IsActive,
    };
}
