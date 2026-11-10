using OrderPayment.Application.Contracts;

namespace OrderPayment.Application.Abstractions;

public interface IProductService
{
    Task<PageResult<ProductResponse>> ListAsync(PageRequest query, CancellationToken ct);
    Task<ProductResponse> GetAsync(Guid id, CancellationToken ct);
    Task<ProductResponse> CreateAsync(ProductRequest request, CancellationToken ct);
    Task<ProductResponse> UpdateAsync(Guid id, ProductRequest request, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}
public interface ICustomerService
{
    Task<PageResult<CustomerResponse>> ListAsync(PageRequest query, CancellationToken ct);
    Task<CustomerResponse> GetAsync(Guid id, CancellationToken ct);
    Task<CustomerResponse> CreateAsync(CustomerRequest request, CancellationToken ct);
    Task<CustomerResponse> UpdateAsync(Guid id, CustomerRequest request, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}
public interface IOrderService
{
    Task<PageResult<OrderResponse>> ListAsync(OrderQuery query, CancellationToken ct);
    Task<OrderResponse> GetAsync(Guid id, CancellationToken ct);
    Task<OrderResponse> CreateAsync(OrderRequest request, CancellationToken ct);
    Task<OrderResponse> UpdateAsync(Guid id, OrderRequest request, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}
