using OrderPayment.Application.Contracts;
using OrderPayment.Domain;

namespace OrderPayment.Application.Services;

internal static class Mapping
{
    public static ProductResponse ToResponse(this Product entity) => new(entity.Id, entity.Name, entity.Sku, entity.Price, entity.StockQuantity);
    public static CustomerResponse ToResponse(this Customer entity) => new(entity.Id, entity.FullName, entity.Email, entity.Phone);
    public static OrderResponse ToResponse(this Order entity) => new(entity.Id, entity.CustomerId, entity.CreatedAt, entity.Status,
        entity.TotalAmount, entity.Items.Select(x => new OrderItemResponse(x.ProductId, x.ProductName, x.UnitPrice, x.Quantity, x.LineTotal)).ToArray());

    public static PageResult<TResult> MapPage<T, TResult>(this PageResult<T> page, Func<T, TResult> map) =>
        new(page.Items.Select(map).ToArray(), page.TotalCount, page.Page, page.PageSize);
}
