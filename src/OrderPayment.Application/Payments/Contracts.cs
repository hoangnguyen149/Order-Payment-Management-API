using OrderPayment.Domain;

namespace OrderPayment.Application.Payments;

public sealed record PaymentCreateRequest(Guid OrderId, string IdempotencyKey);
public sealed record PaymentResponse(Guid Id, Guid OrderId, string PaymentReference, decimal Amount,
    PaymentStatus Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool GatewayUnavailable = false, bool RequiresReview = false);
public enum PaymentCallbackStatus { Unknown, Succeeded, Failed }
public sealed record PaymentCallback(string EventId, string PaymentReference, PaymentCallbackStatus Status, decimal Amount);
public sealed record WebhookResult(bool Duplicate, bool Ignored, bool Processed);
public sealed record GatewayPaymentResponse(string PaymentReference, PaymentStatus Status, decimal Amount);

public interface IPaymentService
{
    Task<PaymentResponse> CreateAsync(Guid orderId, string idempotencyKey, CancellationToken ct);
    Task<IReadOnlyList<PaymentResponse>> GetAsync(Guid orderId, CancellationToken ct);
    Task<WebhookResult> ApplyWebhookAsync(PaymentCallback callback, CancellationToken ct);
}

public interface IPaymentGatewayClient
{
    Task<GatewayPaymentResponse> CreateAsync(string paymentReference, decimal amount, CancellationToken ct);
    Task<GatewayPaymentResponse> GetAsync(string paymentReference, CancellationToken ct);
}

public interface IWebhookSignatureVerifier
{
    bool Verify(ReadOnlySpan<byte> body, string? signature);
}
