using FluentValidation;
using OrderPayment.Application.Contracts;
using OrderPayment.Domain;

namespace OrderPayment.Application.Orders;

public sealed record OrderStatusRequest(OrderStatus Status);

public interface IOrderStatusService
{
    Task<OrderResponse> TransitionAsync(Guid orderId, OrderStatus target, CancellationToken ct);
}

public sealed class OrderStatusRequestValidator : AbstractValidator<OrderStatusRequest>
{
    public OrderStatusRequestValidator()
    {
        // Paid đến từ callback đã xác thực; hủy đơn dùng DELETE để hoàn kho trong transaction.
        RuleFor(x => x.Status).Must(x => x is OrderStatus.Shipped or OrderStatus.Completed)
            .WithMessage("Endpoint này chỉ cho phép chuyển sang Shipped hoặc Completed.");
    }
}
