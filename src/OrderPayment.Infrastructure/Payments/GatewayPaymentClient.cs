using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OrderPayment.Application.Payments;
using OrderPayment.Domain;

namespace OrderPayment.Infrastructure.Payments;

public sealed class GatewayPaymentClient(HttpClient client) : IPaymentGatewayClient
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<GatewayPaymentResponse> CreateAsync(string paymentReference, decimal amount, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "payments")
        {
            Content = JsonContent.Create(new { paymentReference, amount }, options: JsonOptions)
        };
        // Gateway lưu cùng reference nên POST retry không tạo thêm giao dịch.
        request.Headers.Add("Idempotency-Key", paymentReference);
        var payment = await SendAsync(request, paymentReference, ct);
        if (payment.Amount != amount) throw new HttpRequestException("Gateway returned a mismatched payment amount.");
        return payment;
    }

    public async Task<GatewayPaymentResponse> GetAsync(string paymentReference, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "payments/" + Uri.EscapeDataString(paymentReference));
        return await SendAsync(request, paymentReference, ct);
    }

    private async Task<GatewayPaymentResponse> SendAsync(
        HttpRequestMessage request, string expectedReference, CancellationToken ct)
    {
        // Bao cả phần đọc body: resilience timeout HTTP có thể kết thúc khi nhận headers.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            return await ReadResponseAsync(response, expectedReference, deadline.Token);
        }
        catch (OperationCanceledException exception) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException("Gateway operation exceeded its timeout.", exception);
        }
    }

    private static async Task<GatewayPaymentResponse> ReadResponseAsync(
        HttpResponseMessage response, string expectedReference, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        GatewayPaymentWireResponse? result;
        try
        {
            result = await response.Content.ReadFromJsonAsync<GatewayPaymentWireResponse>(JsonOptions, ct);
        }
        catch (JsonException exception)
        {
            throw new HttpRequestException("Gateway returned invalid payment JSON.", exception);
        }

        if (result is null || !string.Equals(result.PaymentReference, expectedReference, StringComparison.Ordinal) ||
            result.Amount <= 0 || result.Amount > 1_000_000_000_000_000m ||
            decimal.Round(result.Amount, 2) != result.Amount || !Enum.IsDefined(result.Status))
            throw new HttpRequestException("Gateway returned an invalid payment response.");
        return new GatewayPaymentResponse(result.PaymentReference, result.Status, result.Amount);
    }

    private sealed class GatewayPaymentWireResponse
    {
        [JsonRequired]
        public string PaymentReference { get; init; } = string.Empty;
        [JsonRequired]
        public PaymentStatus Status { get; init; }
        [JsonRequired]
        public decimal Amount { get; init; }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter<PaymentStatus>(allowIntegerValues: false));
        return options;
    }
}
