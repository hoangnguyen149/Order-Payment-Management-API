namespace OrderPayment.Application.Contracts;

/// <summary>Ngày lịch UTC: From được tính, ToExclusive không được tính.</summary>
public sealed class RevenueQuery
{
    public DateOnly From { get; set; }
    public DateOnly ToExclusive { get; set; }
    public Guid? CustomerId { get; set; }
}

public sealed record RevenueDayResponse(DateOnly Date, int OrderCount, decimal Revenue);
