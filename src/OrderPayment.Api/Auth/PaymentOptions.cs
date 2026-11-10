using System.ComponentModel.DataAnnotations;

namespace OrderPayment.Api.Auth;

public sealed class PaymentOptions
{
    [Required, MinLength(32)] public string WebhookSecret { get; set; } = "";
}
