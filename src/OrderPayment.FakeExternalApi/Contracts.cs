using System.ComponentModel.DataAnnotations;

namespace OrderPayment.FakeExternalApi;

public sealed class FakeOptions
{
    [Required, MinLength(32), MaxLength(200)] public string AdminKey { get; set; } = "";
    [Required] public string DatabasePath { get; set; } = "data/fake-external.db";
    [Required] public string ApiBaseUrl { get; set; } = "http://api:8080";
}

public sealed class FakePaymentOptions
{
    [Required, MinLength(32), MaxLength(200)] public string WebhookSecret { get; set; } = "";
}

public sealed record CreatePaymentRequest(string PaymentReference, decimal Amount);
public sealed record PaymentView(string PaymentReference, string Status, decimal Amount);
public sealed record SettlePaymentRequest(string Status);
public sealed record PaymentRecord(string PaymentReference, string Status, decimal Amount, string? EventId)
{
    public PaymentView View() => new(PaymentReference, Status, Amount);
}
public sealed record PaymentCallback(string EventId, string PaymentReference, string Status, decimal Amount);
public sealed record MarketplaceLine(string Sku, int Quantity);
public sealed record MarketplaceWriteRequest(string ExternalId, string CustomerEmail, string CustomerName,
    List<MarketplaceLine> Items, DateTimeOffset? UpdatedAt = null);
public sealed record MarketplaceOrder(string ExternalId, DateTimeOffset UpdatedAt, string CustomerEmail,
    string CustomerName, IReadOnlyList<MarketplaceLine> Items);
public sealed record MarketplacePage(IReadOnlyList<MarketplaceOrder> Items, string? NextCursor);
public sealed record FeedCursor(long SnapshotRevision, long LastRevision, long SinceTicks, long UntilTicks);

public sealed class SimulatorConflictException(string message) : Exception(message);
