using OrderPayment.Domain;

namespace OrderPayment.Application.Contracts;

public sealed record PageResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
public sealed record ProductResponse(Guid Id, string Name, string Sku, decimal Price, int StockQuantity);
public sealed record CustomerResponse(Guid Id, string FullName, string Email, string? Phone);
public sealed record OrderItemResponse(Guid ProductId, string ProductName, decimal UnitPrice, int Quantity, decimal LineTotal);
public sealed record OrderResponse(Guid Id, Guid CustomerId, DateTimeOffset CreatedAt, OrderStatus Status,
    decimal TotalAmount, IReadOnlyList<OrderItemResponse> Items);
