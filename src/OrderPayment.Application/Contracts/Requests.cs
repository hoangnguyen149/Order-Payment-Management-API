using OrderPayment.Domain;

namespace OrderPayment.Application.Contracts;

public sealed record ProductRequest(string Name, string Sku, decimal Price, int StockQuantity);
public sealed record CustomerRequest(string FullName, string Email, string? Phone);
public sealed record OrderLineRequest(Guid ProductId, int Quantity);
public sealed record OrderRequest(Guid CustomerId, List<OrderLineRequest> Items);

public class PageRequest
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string? Search { get; set; }
    public string? Sort { get; set; }
    public bool Descending { get; set; }
}

public sealed class OrderQuery : PageRequest
{
    public Guid? CustomerId { get; set; }
    public OrderStatus? Status { get; set; }
}
