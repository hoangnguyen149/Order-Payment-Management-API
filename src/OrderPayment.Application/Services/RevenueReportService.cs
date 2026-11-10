using FluentValidation;
using OrderPayment.Application.Abstractions;
using OrderPayment.Application.Contracts;

namespace OrderPayment.Application.Services;

public sealed class RevenueReportService(IRevenueReportReader reader, IValidator<RevenueQuery> validator) : IRevenueReportService
{
    public async Task<IReadOnlyList<RevenueDayResponse>> GetDailyAsync(RevenueQuery query, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(query, ct);
        return await reader.ReadDailyAsync(query.From, query.ToExclusive, query.CustomerId, ct);
    }
}
