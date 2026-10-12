using AiNative.Domain.Common;
using AiNative.Domain.Erp;

namespace AiNative.UnitTests.Domain;

public sealed class ErpOrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Lines_round_amounts_to_cents_and_total_follows_the_lines()
    {
        var order = new ErpSalesOrder { Number = "SO-0001" };

        order.SetLines([ErpOrderLine.Create(Guid.NewGuid(), 3, 0.335m), ErpOrderLine.Create(Guid.NewGuid(), 1.5m, 10)]);

        order.Lines.Select(l => l.Amount).Should().Equal(1.01m, 15m);
        order.Total.Should().Be(16.01m);
    }

    [Fact]
    public void Lines_need_positive_quantities_and_at_least_one_line()
    {
        var order = new ErpSalesOrder { Number = "SO-0001" };

        FluentActions.Invoking(() => ErpOrderLine.Create(Guid.NewGuid(), 0, 1)).Should().Throw<DomainException>().Which.Code.Should().Be("erp_order.invalid_quantity");
        FluentActions.Invoking(() => ErpOrderLine.Create(Guid.NewGuid(), 1, -1)).Should().Throw<DomainException>().Which.Code.Should().Be("erp_order.invalid_price");
        FluentActions.Invoking(() => order.SetLines([])).Should().Throw<DomainException>().Which.Code.Should().Be("erp_order.no_lines");
    }

    [Fact]
    public void Sales_orders_move_draft_confirmed_shipped_and_only_drafts_can_change()
    {
        var order = new ErpSalesOrder { Number = "SO-0001" };
        var product = Guid.NewGuid();
        order.SetLines([ErpOrderLine.Create(product, 1, 1)]);

        FluentActions.Invoking(() => order.Ship(All(order), Now)).Should().Throw<DomainException>();
        order.Confirm(Now);
        order.Status.Should().Be(ErpSalesOrderStatus.Confirmed);
        order.ConfirmedAt.Should().Be(Now);
        FluentActions.Invoking(() => order.SetLines([ErpOrderLine.Create(Guid.NewGuid(), 2, 1)])).Should().Throw<DomainException>();
        order.Ship(All(order), Now);
        order.Status.Should().Be(ErpSalesOrderStatus.Shipped);
        order.ShippedAt.Should().Be(Now);
        FluentActions.Invoking(() => order.Cancel(Now)).Should().Throw<DomainException>();
    }

    [Fact]
    public void Partial_shipments_add_up_and_closing_ends_the_order()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var order = new ErpSalesOrder { Number = "SO-0001" };
        order.SetLines([ErpOrderLine.Create(a, 3, 0.335m), ErpOrderLine.Create(b, 2, 5)]);
        order.Confirm(Now);

        order.Ship(new Dictionary<Guid, decimal> { [a] = 1 }, Now);
        order.Status.Should().Be(ErpSalesOrderStatus.PartiallyShipped);
        order.ShippedAt.Should().BeNull();
        FluentActions.Invoking(() => order.Ship(new Dictionary<Guid, decimal> { [a] = 3 }, Now))
            .Should().Throw<DomainException>().Which.Code.Should().Be("erp_order.invalid_quantity");
        FluentActions.Invoking(() => order.Ship(new Dictionary<Guid, decimal> { [Guid.NewGuid()] = 1 }, Now))
            .Should().Throw<DomainException>().Which.Code.Should().Be("erp_order.unknown_product");
        FluentActions.Invoking(() => order.Cancel(Now)).Should().Throw<DomainException>();

        // 分三次发完 3 × 0.335：各次金额按累计数量算差额，加起来正好是整行的 1.01。
        var line = order.Lines[0];
        new[] { line.AmountBetween(0, 1), line.AmountBetween(1, 2), line.AmountBetween(2, 3) }.Should().Equal(0.34m, 0.33m, 0.34m);

        order.Ship(new Dictionary<Guid, decimal> { [a] = 2, [b] = 1 }, Now);
        order.Lines.Select(l => l.Delivered).Should().Equal(3m, 1m);
        order.Status.Should().Be(ErpSalesOrderStatus.PartiallyShipped);
        order.CloseShipping(Now);
        order.Status.Should().Be(ErpSalesOrderStatus.Shipped);
        order.Lines[1].Remaining.Should().Be(1);
    }

    [Fact]
    public void Returns_never_exceed_what_was_delivered()
    {
        var product = Guid.NewGuid();
        var order = new ErpSalesOrder { Number = "SO-0001" };
        order.SetLines([ErpOrderLine.Create(product, 5, 2)]);
        order.Confirm(Now);

        FluentActions.Invoking(() => order.Return(new Dictionary<Guid, decimal> { [product] = 1 })).Should().Throw<DomainException>();
        order.Ship(new Dictionary<Guid, decimal> { [product] = 3 }, Now);
        order.Return(new Dictionary<Guid, decimal> { [product] = 2 });
        order.Lines[0].Returnable.Should().Be(1);
        FluentActions.Invoking(() => order.Return(new Dictionary<Guid, decimal> { [product] = 2 }))
            .Should().Throw<DomainException>().Which.Code.Should().Be("erp_order.invalid_quantity");
    }

    [Fact]
    public void Purchase_orders_can_be_cancelled_until_received()
    {
        var order = new ErpPurchaseOrder { Number = "PO-0001" };
        order.SetLines([ErpOrderLine.Create(Guid.NewGuid(), 1, 1)]);

        FluentActions.Invoking(() => order.Receive(All(order), Now)).Should().Throw<DomainException>();
        order.Place(Now);
        order.Cancel(Now);
        order.Status.Should().Be(ErpPurchaseOrderStatus.Cancelled);
        order.CancelledAt.Should().Be(Now);
        FluentActions.Invoking(() => order.Place(Now)).Should().Throw<DomainException>();
    }

    [Fact]
    public void Purchase_orders_receive_in_batches_and_return_received_goods()
    {
        var product = Guid.NewGuid();
        var order = new ErpPurchaseOrder { Number = "PO-0001" };
        order.SetLines([ErpOrderLine.Create(product, 10, 3)]);
        order.Place(Now);

        order.Receive(new Dictionary<Guid, decimal> { [product] = 4 }, Now);
        order.Status.Should().Be(ErpPurchaseOrderStatus.PartiallyReceived);
        FluentActions.Invoking(() => order.Cancel(Now)).Should().Throw<DomainException>();
        order.Return(new Dictionary<Guid, decimal> { [product] = 1 });
        order.Receive(All(order), Now);
        order.Status.Should().Be(ErpPurchaseOrderStatus.Received);
        order.ReceivedAt.Should().Be(Now);
        order.Lines[0].Returnable.Should().Be(9);
        FluentActions.Invoking(() => order.CloseReceiving(Now)).Should().Throw<DomainException>();
    }

    private static Dictionary<Guid, decimal> All(ErpSalesOrder order) => order.Lines.ToDictionary(l => l.ProductId, l => l.Remaining);

    private static Dictionary<Guid, decimal> All(ErpPurchaseOrder order) => order.Lines.ToDictionary(l => l.ProductId, l => l.Remaining);
}
