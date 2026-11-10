using System.Globalization;
using System.Net.Http.Json;
using OrderPayment.Application.Marketplace;

namespace OrderPayment.Infrastructure.Jobs;

internal sealed class MarketplaceClient(HttpClient http) : IMarketplaceClient
{
    public async Task<MarketplacePage> GetOrdersAsync(DateTimeOffset since, DateTimeOffset until, string? cursor, CancellationToken ct)
    {
        var path = "marketplace/orders?since=" + Uri.EscapeDataString(since.ToString("O", CultureInfo.InvariantCulture))
            + "&until=" + Uri.EscapeDataString(until.ToString("O", CultureInfo.InvariantCulture)) + "&pageSize=100";
        if (cursor is not null) path += "&cursor=" + Uri.EscapeDataString(cursor);
        var page = await http.GetFromJsonAsync<MarketplacePage>(path, ct)
            ?? throw new InvalidOperationException("Marketplace returned an empty response.");
        if (page.Items is null || page.Items.Count > 100 || page.NextCursor?.Length > 2048)
            throw new InvalidOperationException("Marketplace returned an invalid page.");
        return page;
    }
}
