using OrderPayment.Application.Contracts;

namespace OrderPayment.Application.Abstractions;

public interface IRevenueReportReader
{
    Task<IReadOnlyList<RevenueDayResponse>> ReadDailyAsync(
        DateOnly from, DateOnly toExclusive, Guid? customerId, CancellationToken ct);
}

public interface IRevenueReportService
{
    Task<IReadOnlyList<RevenueDayResponse>> GetDailyAsync(RevenueQuery query, CancellationToken ct);
}
