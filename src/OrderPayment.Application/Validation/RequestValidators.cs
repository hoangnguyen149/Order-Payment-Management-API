using FluentValidation;
using OrderPayment.Application.Contracts;

namespace OrderPayment.Application.Validation;

public sealed class ProductRequestValidator : AbstractValidator<ProductRequest>
{
    public ProductRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Sku).NotEmpty().MaximumLength(64).Matches(@"^[A-Za-z0-9][A-Za-z0-9_-]*$");
        RuleFor(x => x.Price).GreaterThan(0).LessThanOrEqualTo(1_000_000_000m).PrecisionScale(18, 2, false);
        RuleFor(x => x.StockQuantity).GreaterThanOrEqualTo(0);
    }
}

public sealed class CustomerRequestValidator : AbstractValidator<CustomerRequest>
{
    public CustomerRequestValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(254);
        RuleFor(x => x.Phone).MaximumLength(30).Matches(@"^[+0-9 ().-]*$").When(x => x.Phone is not null);
    }
}

public sealed class OrderLineRequestValidator : AbstractValidator<OrderLineRequest>
{
    public OrderLineRequestValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.Quantity).InclusiveBetween(1, 10000);
    }
}

public sealed class OrderRequestValidator : AbstractValidator<OrderRequest>
{
    public OrderRequestValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.Items).Cascade(CascadeMode.Stop)
            .NotNull()
            .Must(lines => lines.Count is >= 1 and <= 100).WithMessage("Đơn phải có từ 1 đến 100 dòng.")
            .Must(lines => lines.All(line => line is not null)).WithMessage("Dòng đơn không được null.")
            .Must(lines => lines.Select(line => line.ProductId).Distinct().Count() == lines.Count)
            .WithMessage("Mỗi sản phẩm chỉ được xuất hiện một lần trong đơn.");
        RuleForEach(x => x.Items).SetValidator(new OrderLineRequestValidator());
    }
}

public abstract class PageRequestValidator<T> : AbstractValidator<T> where T : PageRequest
{
    public PageRequestValidator()
    {
        RuleFor(x => x.Page).InclusiveBetween(1, 1_000_000);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Search).MaximumLength(100);
        RuleFor(x => x.Sort).MaximumLength(30);
    }
}

public sealed class PageRequestValidator : PageRequestValidator<PageRequest>;

public sealed class OrderQueryValidator : PageRequestValidator<OrderQuery>
{
    public OrderQueryValidator()
    {
        RuleFor(x => x.CustomerId).Must(x => !x.HasValue || x.Value != Guid.Empty);
        RuleFor(x => x.Search).Must(x => string.IsNullOrEmpty(x) || Guid.TryParse(x, out _))
            .WithMessage("Search đơn hàng phải là GUID đầy đủ.");
        RuleFor(x => x.Status).IsInEnum().When(x => x.Status.HasValue);
    }
}
