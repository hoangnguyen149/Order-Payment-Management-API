using FluentAssertions;
using FluentValidation;
using Moq;
using OrderPayment.Application.Abstractions;
using OrderPayment.Application.Contracts;
using OrderPayment.Application.Services;
using OrderPayment.Application.Validation;
using Xunit;

namespace OrderPayment.UnitTests;

public sealed class ReportServiceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(367)]
    public async Task Invalid_range_never_reaches_the_database(int days)
    {
        var reader = new Mock<IRevenueReportReader>(MockBehavior.Strict);
        var service = CreateService(reader);
        var start = new DateOnly(2024, 1, 1);
        var query = new RevenueQuery { From = start, ToExclusive = start.AddDays(days) };

        Func<Task> report = () => service.GetDailyAsync(query, CancellationToken.None);

        await report.Should().ThrowAsync<ValidationException>();
        reader.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_date_is_rejected_before_querying_the_database(bool missingFrom)
    {
        var reader = new Mock<IRevenueReportReader>(MockBehavior.Strict);
        var query = new RevenueQuery
        {
            From = missingFrom ? default : new DateOnly(2024, 1, 1),
            ToExclusive = missingFrom ? new DateOnly(2024, 1, 2) : default
        };

        Func<Task> report = () => CreateService(reader).GetDailyAsync(query, CancellationToken.None);

        await report.Should().ThrowAsync<ValidationException>();
        reader.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Empty_customer_filter_is_rejected_instead_of_silently_becoming_an_unfiltered_report()
    {
        var reader = new Mock<IRevenueReportReader>(MockBehavior.Strict);
        var query = new RevenueQuery
        {
            From = new(2024, 1, 1), ToExclusive = new(2024, 2, 1), CustomerId = Guid.Empty
        };

        Func<Task> report = () => CreateService(reader).GetDailyAsync(query, CancellationToken.None);

        await report.Should().ThrowAsync<ValidationException>();
        reader.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Full_leap_year_range_preserves_utc_boundaries_customer_filter_and_cancellation(bool filtered)
    {
        var reader = new Mock<IRevenueReportReader>(MockBehavior.Strict);
        var from = new DateOnly(2024, 1, 1);
        var toExclusive = new DateOnly(2025, 1, 1);
        Guid? customer = filtered ? Guid.NewGuid() : null;
        using var source = new CancellationTokenSource();
        IReadOnlyList<RevenueDayResponse> days = [new(new DateOnly(2024, 2, 29), 3, 123.45m)];
        reader.Setup(x => x.ReadDailyAsync(from, toExclusive, customer, source.Token)).ReturnsAsync(days);

        var response = await CreateService(reader).GetDailyAsync(new RevenueQuery
        {
            From = from, ToExclusive = toExclusive, CustomerId = customer
        }, source.Token);

        response.Should().BeSameAs(days);
        reader.VerifyAll();
        reader.VerifyNoOtherCalls();
    }

    private static RevenueReportService CreateService(Mock<IRevenueReportReader> reader) =>
        new(reader.Object, new RevenueQueryValidator());
}
