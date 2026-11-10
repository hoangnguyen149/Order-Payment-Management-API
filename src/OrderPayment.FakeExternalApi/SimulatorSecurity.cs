using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace OrderPayment.FakeExternalApi;

public sealed class SimulatorSecurity(IOptions<FakeOptions> options)
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(options.Value.AdminKey);

    public bool Authorize(string supplied) => supplied.Length <= 200 && CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(_key));

    public string EncodeCursor(FeedCursor cursor)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(cursor, SimulatorStore.JsonOptions);
        return WebEncoders.Base64UrlEncode(json) + "." + Convert.ToHexString(HMACSHA256.HashData(_key, json));
    }

    public FeedCursor? DecodeCursor(string token, DateTimeOffset since, DateTimeOffset until)
    {
        if (token.Length > 1024) return null;
        var parts = token.Split('.');
        if (parts.Length != 2 || parts[1].Length != 64) return null;
        try
        {
            var json = WebEncoders.Base64UrlDecode(parts[0]);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(parts[1]), HMACSHA256.HashData(_key, json))) return null;
            var cursor = JsonSerializer.Deserialize<FeedCursor>(json, SimulatorStore.JsonOptions);
            return cursor is { SnapshotRevision: >= 0, LastRevision: >= 0 } && cursor.LastRevision <= cursor.SnapshotRevision
                && cursor.SinceTicks == since.UtcTicks && cursor.UntilTicks == until.UtcTicks ? cursor : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException) { return null; }
    }
}

public sealed class CallbackSender(HttpClient client, IOptions<FakePaymentOptions> options)
{
    public async Task<bool> SendAsync(PaymentRecord payment, CancellationToken ct)
    {
        if (payment.EventId is null) return false;
        var payload = new PaymentCallback(payment.EventId, payment.PaymentReference, payment.Status, payment.Amount);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, SimulatorStore.JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/webhooks/payment");
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        request.Headers.Add("X-Payment-Signature", Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(options.Value.WebhookSecret), bytes)));
        try
        {
            using var response = await client.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }
}
