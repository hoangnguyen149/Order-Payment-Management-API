using FluentAssertions;
using OrderPayment.Domain;
using Xunit;

namespace OrderPayment.UnitTests;

public sealed class DomainTests
{
    [Fact]
    public void Order_calculates_decimal_total_from_price_snapshots()
    {
        var product = new Product("Keyboard", "kb", 19.99m, 10);
        var order = new Order(Guid.NewGuid(),
        [
            new OrderItem(product.Id, product.Name, product.Price, 3),
            new OrderItem(Guid.NewGuid(), "Mouse", 10.15m, 2)
        ]);

        product.Update("Keyboard v2", "kb", 29.99m, 10);

        order.TotalAmount.Should().Be(80.27m);
        order.Items[0].UnitPrice.Should().Be(19.99m);
        order.Items.Should().OnlyContain(x => x.OrderId == order.Id);
    }

    [Fact]
    public void Replacing_pending_order_recalculates_total_and_customer()
    {
        var order = CreateOrder();
        var customer = Guid.NewGuid();

        order.ReplaceItems(customer, [new OrderItem(Guid.NewGuid(), "Replacement", 2.25m, 4)]);

        order.CustomerId.Should().Be(customer);
        order.TotalAmount.Should().Be(9m);
        order.Items.Should().ContainSingle();
    }

    [Fact]
    public void Exposed_order_items_cannot_bypass_total_calculation()
    {
        var input = new List<OrderItem> { new(Guid.NewGuid(), "A", 5m, 2) };
        var order = new Order(Guid.NewGuid(), input);
        var exposed = (ICollection<OrderItem>)order.Items;

        input.Clear();
        Action clear = () => exposed.Clear();
        Action add = () => exposed.Add(new OrderItem(Guid.NewGuid(), "Injected", 999m, 1));

        exposed.IsReadOnly.Should().BeTrue();
        clear.Should().Throw<NotSupportedException>();
        add.Should().Throw<NotSupportedException>();
        order.Items.Should().ContainSingle();
        order.TotalAmount.Should().Be(10m);
    }

    [Fact]
    public void Replacing_items_does_not_share_mutable_lists_with_callers_or_old_views()
    {
        var order = CreateOrder();
        var oldView = order.Items;
        var replacement = new List<OrderItem> { new(Guid.NewGuid(), "Replacement", 3m, 3) };

        order.ReplaceItems(order.CustomerId, replacement);
        replacement.Clear();
        Action mutateOldView = () => ((ICollection<OrderItem>)oldView).Clear();

        mutateOldView.Should().Throw<NotSupportedException>();
        oldView.Should().ContainSingle().Which.ProductName.Should().Be("A");
        order.Items.Should().ContainSingle().Which.ProductName.Should().Be("Replacement");
        order.TotalAmount.Should().Be(9m);
    }

    [Fact]
    public void Duplicate_product_lines_are_rejected()
    {
        var id = Guid.NewGuid();
        Action create = () => new Order(Guid.NewGuid(),
            [new OrderItem(id, "A", 10m, 1), new OrderItem(id, "A", 10m, 2)]);

        create.Should().Throw<DomainRuleException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10001)]
    public void Invalid_quantities_are_rejected(int quantity)
    {
        Action create = () => new OrderItem(Guid.NewGuid(), "A", 10m, quantity);
        create.Should().Throw<DomainRuleException>();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.01")]
    [InlineData("1.001")]
    [InlineData("1000000000.01")]
    public void Invalid_money_values_are_rejected(string value)
    {
        var price = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        Action create = () => new Product("A", "sku", price, 10);
        create.Should().Throw<DomainRuleException>();
    }

    [Fact]
    public void Reserving_last_item_succeeds_and_second_reservation_leaves_stock_unchanged()
    {
        var product = new Product("A", "sku", 10m, 1);
        product.Reserve(1);

        Action reserveAgain = () => product.Reserve(1);

        reserveAgain.Should().Throw<DomainRuleException>();
        product.StockQuantity.Should().Be(0);
        product.Release(1);
        product.StockQuantity.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(Transitions))]
    public void Order_enforces_transition_graph(OrderStatus current, OrderStatus next, bool allowed)
    {
        var order = AtStatus(current);
        Action transition = () => order.ChangeStatus(next);

        if (allowed)
        {
            transition.Should().NotThrow();
            order.Status.Should().Be(next);
        }
        else
        {
            transition.Should().Throw<DomainRuleException>();
            order.Status.Should().Be(current);
        }
    }

    [Theory]
    [InlineData(OrderStatus.Paid)]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Completed)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.PaymentFailed)]
    public void Only_pending_orders_can_be_edited(OrderStatus status)
    {
        var order = AtStatus(status);
        Action edit = () => order.ReplaceItems(Guid.NewGuid(), [new OrderItem(Guid.NewGuid(), "B", 2m, 1)]);
        edit.Should().Throw<DomainRuleException>();
    }

    public static IEnumerable<object[]> Transitions()
    {
        foreach (var current in Enum.GetValues<OrderStatus>())
        foreach (var next in Enum.GetValues<OrderStatus>())
        {
            var allowed = (current, next) switch
            {
                (OrderStatus.Pending, OrderStatus.Paid or OrderStatus.Cancelled or OrderStatus.PaymentFailed) => true,
                (OrderStatus.Paid, OrderStatus.Shipped) => true,
                (OrderStatus.Shipped, OrderStatus.Completed) => true,
                _ => false
            };
            yield return [current, next, allowed];
        }
    }

    private static Order CreateOrder() => new(Guid.NewGuid(), [new OrderItem(Guid.NewGuid(), "A", 5m, 2)]);

    private static Order AtStatus(OrderStatus status)
    {
        var order = CreateOrder();
        if (status == OrderStatus.Pending) return order;
        if (status is OrderStatus.Cancelled or OrderStatus.PaymentFailed)
        {
            order.ChangeStatus(status);
            return order;
        }
        order.ChangeStatus(OrderStatus.Paid);
        if (status == OrderStatus.Paid) return order;
        order.ChangeStatus(OrderStatus.Shipped);
        if (status == OrderStatus.Completed) order.ChangeStatus(OrderStatus.Completed);
        return order;
    }
}
