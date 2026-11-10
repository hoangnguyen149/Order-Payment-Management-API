using System.Text;
using FluentAssertions;
using OrderPayment.Application.Payments;
using Xunit;

namespace OrderPayment.UnitTests;

public sealed class PaymentSignatureTests
{
    private const string Secret = "12345678901234567890123456789012";
    private const string Body = "{\"eventId\":\"evt-1\",\"note\":\"Đã thanh toán\"}";
    // Vector được tính độc lập bằng Python hmac+hashlib, không serialize lại nội dung JSON.
    private const string Expected = "3d60d0538797c716097e14a522b97ea28c4d3cd94719150126ee821734abfbbe";

    [Fact]
    public void Known_utf8_signature_is_valid_in_lowercase_or_uppercase_hex()
    {
        var verifier = new PaymentSignatureVerifier(Secret);
        verifier.Verify(Encoding.UTF8.GetBytes(Body), Expected).Should().BeTrue();
        verifier.Verify(Encoding.UTF8.GetBytes(Body), Expected.ToUpperInvariant()).Should().BeTrue();
    }

    [Theory]
    [InlineData("{ \"eventId\":\"evt-1\",\"note\":\"Đã thanh toán\"}")]
    [InlineData("{\"eventId\":\"evt-1\",\"note\":\"Đã thanh toán\"}\n")]
    [InlineData("{\"eventId\":\"evt-2\",\"note\":\"Đã thanh toán\"}")]
    [InlineData("")]
    public void Any_body_change_invalidates_the_signature(string changed)
    {
        new PaymentSignatureVerifier(Secret).Verify(Encoding.UTF8.GetBytes(changed), Expected).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=3d60d0538797c716097e14a522b97ea28c4d3cd94719150126ee821734abfbbe")]
    [InlineData("3d60d0538797c716097e14a522b97ea28c4d3cd94719150126ee821734abfbb")]
    [InlineData("zz60d0538797c716097e14a522b97ea28c4d3cd94719150126ee821734abfbbe")]
    [InlineData("3d60d0538797c716097e14a522b97ea28c4d3cd94719150126ee821734abfbbf")]
    public void Missing_malformed_or_incorrect_signature_is_rejected(string? signature)
    {
        new PaymentSignatureVerifier(Secret).Verify(Encoding.UTF8.GetBytes(Body), signature).Should().BeFalse();
    }

    [Fact]
    public void Correct_signature_with_a_different_secret_is_rejected()
    {
        new PaymentSignatureVerifier("another-development-secret-32-bytes").Verify(Encoding.UTF8.GetBytes(Body), Expected)
            .Should().BeFalse();
    }

    [Fact]
    public void Short_or_null_secret_fails_configuration_instead_of_accepting_an_empty_key()
    {
        Action shortSecret = () => new PaymentSignatureVerifier("short");
        Action nullSecret = () => new PaymentSignatureVerifier(null!);
        shortSecret.Should().Throw<ArgumentException>();
        nullSecret.Should().Throw<ArgumentNullException>();
    }
}
