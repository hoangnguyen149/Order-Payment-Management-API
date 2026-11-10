using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderPayment.Domain;
using OrderPayment.Infrastructure.Persistence;
using Xunit;

namespace OrderPayment.IntegrationTests;

[Collection("SqlServer API")]
public sealed class ReportingTests(ApiFixture fixture)
{
    [Fact]
    public async Task Migrations_install_report_procedure_and_covering_indexes()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderPaymentDbContext>();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.procedures WHERE object_id = OBJECT_ID(N'dbo.usp_GetDailyRevenue');";
        Convert.ToInt32(await command.ExecuteScalarAsync()).Should().Be(1);

        command.CommandText = """
            SELECT COUNT(*)
            FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'dbo.Orders')
              AND name IN (N'IX_Orders_CreatedAt_Revenue', N'IX_Orders_CustomerId_CreatedAt');
            """;
        Convert.ToInt32(await command.ExecuteScalarAsync()).Should().Be(2);

        command.CommandText = """
            SELECT COUNT(*)
            FROM sys.indexes AS i
            INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(N'dbo.Orders')
              AND i.name = N'IX_Orders_CreatedAt_Revenue'
              AND ic.is_included_column = 1
              AND c.name IN (N'CustomerId', N'Status', N'TotalAmount');
            """;
        Convert.ToInt32(await command.ExecuteScalarAsync()).Should().Be(3);
    }

    [Fact]
    public async Task Daily_revenue_counts_orders_once_filters_status_and_uses_UTC_exclusive_bounds()
    {
        var customerId = await SeedReportScenario();
        using var client = await fixture.AuthenticatedClient();
        var all = await client.GetFromJsonAsync<List<ReportDay>>(
            "/api/reports/revenue/daily?from=2031-04-01&toExclusive=2031-04-03");

        all.Should().NotBeNull();
        all.Should().HaveCount(2);
        all![0].Should().Be(new ReportDay(new DateOnly(2031, 4, 1), 4, 125.36m));
        all[1].Should().Be(new ReportDay(new DateOnly(2031, 4, 2), 1, 9.99m));

        var filtered = await client.GetFromJsonAsync<List<ReportDay>>(
            $"/api/reports/revenue/daily?from=2031-04-01&toExclusive=2031-04-03&customerId={customerId}");
        filtered.Should().BeEquivalentTo(new[]
        {
            new ReportDay(new DateOnly(2031, 4, 1), 3, 25.36m),
            new ReportDay(new DateOnly(2031, 4, 2), 1, 9.99m)
        }, options => options.WithStrictOrdering());

        var unknownCustomer = await client.GetFromJsonAsync<List<ReportDay>>(
            $"/api/reports/revenue/daily?from=2031-04-01&toExclusive=2031-04-03&customerId={Guid.NewGuid()}");
        unknownCustomer.Should().BeEmpty();
    }

    [Theory]
    [InlineData("from=2031-04-03&toExclusive=2031-04-01")]
    [InlineData("from=2031-04-01&toExclusive=2031-04-01")]
    [InlineData("from=2031-01-01&toExclusive=2032-01-03")]
    [InlineData("from=not-a-date&toExclusive=2031-04-03")]
    [InlineData("toExclusive=2031-04-03")]
    [InlineData("from=2031-04-01&toExclusive=2031-04-03&customerId=00000000-0000-0000-0000-000000000000")]
    public async Task Invalid_reporting_query_returns_validation_problem(string query)
    {
        using var client = await fixture.AuthenticatedClient();
        var response = await client.GetAsync("/api/reports/revenue/daily?" + query);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Staff_cannot_read_revenue_report()
    {
        using var staff = await fixture.AuthenticatedClient("Staff");
        var response = await staff.GetAsync("/api/reports/revenue/daily?from=2031-04-01&toExclusive=2031-04-03");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Independent_contexts_reject_stale_product_rowversion()
    {
        Guid id;
        await using (var seedScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var seed = seedScope.ServiceProvider.GetRequiredService<OrderPaymentDbContext>();
            var product = new Product("Last item", "RV-" + Guid.NewGuid().ToString("N"), 10m, 1);
            seed.Products.Add(product);
            await seed.SaveChangesAsync();
            id = product.Id;
        }

        await using var firstScope = fixture.Factory.Services.CreateAsyncScope();
        await using var secondScope = fixture.Factory.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<OrderPaymentDbContext>();
        var second = secondScope.ServiceProvider.GetRequiredService<OrderPaymentDbContext>();
        var firstProduct = await first.Products.SingleAsync(x => x.Id == id);
        var secondProduct = await second.Products.SingleAsync(x => x.Id == id);
        firstProduct.RowVersion.Should().Equal(secondProduct.RowVersion);

        firstProduct.Reserve(1);
        secondProduct.Reserve(1);
        await first.SaveChangesAsync();
        Func<Task> staleUpdate = () => second.SaveChangesAsync();
        await staleUpdate.Should().ThrowAsync<DbUpdateConcurrencyException>();

        await using var verifyScope = fixture.Factory.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<OrderPaymentDbContext>();
        (await verify.Products.AsNoTracking().SingleAsync(x => x.Id == id)).StockQuantity.Should().Be(0);
    }

    private async Task<Guid> SeedReportScenario()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderPaymentDbContext>();
        var customerA = new Customer("Report A", $"{Guid.NewGuid():N}@example.test", null);
        var customerB = new Customer("Report B", $"{Guid.NewGuid():N}@example.test", null);
        var productA = new Product("Report product A", "RA-" + Guid.NewGuid().ToString("N"), 10.25m, 10000);
        var productB = new Product("Report product B", "RB-" + Guid.NewGuid().ToString("N"), 0.10m, 10000);
        db.Customers.AddRange(customerA, customerB);
        db.Products.AddRange(productA, productB);

        void Add(Guid customer, string timestamp, OrderStatus status, params OrderItem[] items)
        {
            var order = new Order(customer, items);
            if (status is OrderStatus.Paid or OrderStatus.Shipped or OrderStatus.Completed)
            {
                order.ChangeStatus(OrderStatus.Paid);
                if (status is OrderStatus.Shipped or OrderStatus.Completed) order.ChangeStatus(OrderStatus.Shipped);
                if (status == OrderStatus.Completed) order.ChangeStatus(OrderStatus.Completed);
            }
            else if (status is OrderStatus.Cancelled or OrderStatus.PaymentFailed) order.ChangeStatus(status);
            db.Orders.Add(order);
            db.Entry(order).Property(x => x.CreatedAt).CurrentValue = DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture);
        }

        OrderItem Line(decimal price, int quantity = 1) => new(productA.Id, productA.Name, price, quantity);
        Add(customerA.Id, "2031-04-01T00:00:00+00:00", OrderStatus.Paid,
            Line(10.25m, 2), new OrderItem(productB.Id, productB.Name, 0.10m, 3));
        Add(customerA.Id, "2031-04-01T10:00:00+00:00", OrderStatus.Shipped, Line(1.23m));
        Add(customerA.Id, "2031-04-02T14:00:00+00:00", OrderStatus.Completed, Line(9.99m));
        Add(customerB.Id, "2031-04-01T12:00:00+00:00", OrderStatus.Paid, Line(100m));
        // 01:00 giờ +07 vẫn thuộc ngày 01/04 UTC.
        Add(customerA.Id, "2031-04-02T01:00:00+07:00", OrderStatus.Paid, Line(3.33m));
        Add(customerA.Id, "2031-04-01T12:00:00+00:00", OrderStatus.Pending, Line(7m));
        Add(customerA.Id, "2031-04-01T12:00:00+00:00", OrderStatus.Cancelled, Line(8m));
        Add(customerA.Id, "2031-04-01T12:00:00+00:00", OrderStatus.PaymentFailed, Line(9m));
        Add(customerA.Id, "2031-03-31T23:59:59.9999999+00:00", OrderStatus.Completed, Line(999m));
        Add(customerA.Id, "2031-04-03T00:00:00+00:00", OrderStatus.Completed, Line(888m));
        await db.SaveChangesAsync();
        return customerA.Id;
    }

    public sealed record ReportDay(DateOnly Date, int OrderCount, decimal Revenue);
}
