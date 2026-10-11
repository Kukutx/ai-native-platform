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
        order.SetLines([ErpOrderLine.Create(Guid.NewGuid(), 1, 1)]);

        FluentActions.Invoking(() => order.Ship(Now)).Should().Throw<DomainException>();
        order.Confirm(Now);
        order.Status.Should().Be(ErpSalesOrderStatus.Confirmed);
        order.ConfirmedAt.Should().Be(Now);
        FluentActions.Invoking(() => order.SetLines([ErpOrderLine.Create(Guid.NewGuid(), 2, 1)])).Should().Throw<DomainException>();
        order.Ship(Now);
        order.Status.Should().Be(ErpSalesOrderStatus.Shipped);
        FluentActions.Invoking(() => order.Cancel(Now)).Should().Throw<DomainException>();
    }

    [Fact]
    public void Purchase_orders_can_be_cancelled_until_received()
    {
        var order = new ErpPurchaseOrder { Number = "PO-0001" };
        order.SetLines([ErpOrderLine.Create(Guid.NewGuid(), 1, 1)]);

        FluentActions.Invoking(() => order.Receive(Now)).Should().Throw<DomainException>();
        order.Place(Now);
        order.Cancel(Now);
        order.Status.Should().Be(ErpPurchaseOrderStatus.Cancelled);
        order.CancelledAt.Should().Be(Now);
        FluentActions.Invoking(() => order.Place(Now)).Should().Throw<DomainException>();
    }
}
