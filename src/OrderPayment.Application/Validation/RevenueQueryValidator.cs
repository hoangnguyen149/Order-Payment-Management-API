using FluentValidation;
using OrderPayment.Application.Contracts;

namespace OrderPayment.Application.Validation;

public sealed class RevenueQueryValidator : AbstractValidator<RevenueQuery>
{
    public RevenueQueryValidator()
    {
        RuleFor(x => x.From).NotEqual(default(DateOnly)).WithMessage("From là ngày UTC bắt buộc.");
        RuleFor(x => x.ToExclusive).Cascade(CascadeMode.Stop)
            .NotEqual(default(DateOnly)).WithMessage("ToExclusive là ngày UTC bắt buộc.")
            .GreaterThan(x => x.From).WithMessage("ToExclusive phải sau From.")
            .Must((query, end) => end.DayNumber - query.From.DayNumber <= 366)
            .WithMessage("Mỗi báo cáo chỉ được truy vấn tối đa 366 ngày UTC.");
        RuleFor(x => x.CustomerId).Must(x => !x.HasValue || x.Value != Guid.Empty)
            .WithMessage("CustomerId phải là GUID khác rỗng.");
    }
}
