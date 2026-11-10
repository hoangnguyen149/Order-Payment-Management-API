using OrderPayment.Application.Contracts;
using OrderPayment.Domain;

namespace OrderPayment.Application.Abstractions;

public interface IProductRepository
{
    Task<PageResult<Product>> ListAsync(PageRequest query, CancellationToken ct);
    Task<Product?> GetAsync(Guid id, CancellationToken ct);
    Task AddAsync(Product product, CancellationToken ct);
    Task UpdateAsync(Product product, CancellationToken ct);
    Task DeleteAsync(Product product, CancellationToken ct);
}

public interface ICustomerRepository
{
    Task<PageResult<Customer>> ListAsync(PageRequest query, CancellationToken ct);
    Task<Customer?> GetAsync(Guid id, CancellationToken ct);
    Task AddAsync(Customer customer, CancellationToken ct);
    Task UpdateAsync(Customer customer, CancellationToken ct);
    Task DeleteAsync(Customer customer, CancellationToken ct);
}

public interface IOrderRepository
{
    Task<PageResult<Order>> ListAsync(OrderQuery query, CancellationToken ct);
    Task<Order?> GetAsync(Guid id, CancellationToken ct);
    // Mỗi phương thức ghi phải cập nhật tồn kho và đơn trong một transaction.
    Task<Order> CreateAsync(OrderRequest request, CancellationToken ct);
    Task<Order> UpdatePendingAsync(Guid id, OrderRequest request, CancellationToken ct);
    Task CancelAsync(Guid id, CancellationToken ct);
}
