using FluentValidation;

namespace OrderPayment.Application.Payments;

public sealed class PaymentCreateRequestValidator : AbstractValidator<PaymentCreateRequest>
{
    public PaymentCreateRequestValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(100).Matches(@"^[A-Za-z0-9._:-]+$");
    }
}

public sealed class PaymentCallbackValidator : AbstractValidator<PaymentCallback>
{
    public PaymentCallbackValidator()
    {
        RuleFor(x => x.EventId).NotEmpty().MaximumLength(128).Matches(@"^[A-Za-z0-9._:-]+$");
        RuleFor(x => x.PaymentReference).NotEmpty().MaximumLength(64).Matches(@"^[A-Za-z0-9_-]+$");
        RuleFor(x => x.Status).IsInEnum().NotEqual(PaymentCallbackStatus.Unknown);
        RuleFor(x => x.Amount).GreaterThan(0).LessThanOrEqualTo(1_000_000_000_000_000m).PrecisionScale(18, 2, false);
    }
}
