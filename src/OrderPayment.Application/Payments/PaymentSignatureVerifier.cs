using System.Security.Cryptography;
using System.Text;

namespace OrderPayment.Application.Payments;

public sealed class PaymentSignatureVerifier : IWebhookSignatureVerifier
{
    private readonly byte[] _key;

    public PaymentSignatureVerifier(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        _key = Encoding.UTF8.GetBytes(secret);
        if (_key.Length < 32) throw new ArgumentException("Webhook secret phải có ít nhất 32 byte UTF-8.", nameof(secret));
    }

    public bool Verify(ReadOnlySpan<byte> body, string? signature)
    {
        // Ký đúng byte nhận được: deserialize/serialize hoặc thay whitespace sẽ làm thay đổi HMAC.
        if (signature is null || signature.Length != 64) return false;
        byte[] received;
        try { received = Convert.FromHexString(signature); }
        catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(_key, body);
        return CryptographicOperations.FixedTimeEquals(expected, received);
    }
}
