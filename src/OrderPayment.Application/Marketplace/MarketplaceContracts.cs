namespace OrderPayment.Application.Marketplace;

public sealed record MarketplaceLine(string Sku, int Quantity);
public sealed record MarketplaceOrder(string ExternalId, DateTimeOffset UpdatedAt, string CustomerEmail,
    string CustomerName, IReadOnlyList<MarketplaceLine> Items);
public sealed record MarketplacePage(IReadOnlyList<MarketplaceOrder> Items, string? NextCursor);

public interface IMarketplaceClient
{
    Task<MarketplacePage> GetOrdersAsync(DateTimeOffset since, DateTimeOffset until, string? cursor, CancellationToken ct);
}
