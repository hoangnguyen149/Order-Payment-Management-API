using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using OrderPayment.Application;
using OrderPayment.Application.Contracts;
using OrderPayment.Application.Orders;
using OrderPayment.Domain;
using OrderPayment.Infrastructure.Persistence;

namespace OrderPayment.Infrastructure.Orders;

internal sealed class OrderStatusService(OrderPaymentDbContext db, IValidator<OrderStatusRequest> validator) : IOrderStatusService
{
    public async Task<OrderResponse> TransitionAsync(Guid id, OrderStatus target, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(new OrderStatusRequest(target), ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await DatabaseLocks.AcquireOrderAsync(db, id, ct);
        var order = await db.Orders.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new NotFoundException("Không tìm thấy đơn hàng.");
        try { order.ChangeStatus(target); }
        catch (DomainRuleException error) { throw new ConflictException(error.Message); }
        db.OutboxMessages.Add(new OutboxMessage("OrderStatusChanged",
            JsonSerializer.Serialize(new { order.Id, Status = order.Status.ToString(), order.TotalAmount })));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new OrderResponse(order.Id, order.CustomerId, order.CreatedAt, order.Status, order.TotalAmount,
            order.Items.Select(x => new OrderItemResponse(x.ProductId, x.ProductName, x.UnitPrice, x.Quantity, x.LineTotal)).ToArray());
    }
}
