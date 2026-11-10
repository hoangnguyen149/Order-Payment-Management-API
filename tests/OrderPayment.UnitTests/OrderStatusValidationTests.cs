using FluentAssertions;
using OrderPayment.Application.Orders;
using OrderPayment.Domain;
using Xunit;

namespace OrderPayment.UnitTests;

public sealed class OrderStatusValidationTests
{
    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.Paid)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.PaymentFailed)]
    [InlineData((OrderStatus)999)]
    public void Status_endpoint_cannot_bypass_payment_or_cancellation_workflows(OrderStatus target)
    {
        var validation = new OrderStatusRequestValidator().Validate(new OrderStatusRequest(target));
        validation.IsValid.Should().BeFalse();
        validation.Errors.Should().Contain(x => x.PropertyName == nameof(OrderStatusRequest.Status));
    }

    [Theory]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Completed)]
    public void Fulfilment_targets_are_allowed_for_service_to_check_the_transition(OrderStatus target)
    {
        new OrderStatusRequestValidator().Validate(new OrderStatusRequest(target)).IsValid.Should().BeTrue();
    }
}
