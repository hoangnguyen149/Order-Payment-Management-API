using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OrderPayment.Infrastructure.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<OrderPaymentDbContext>
{
    public OrderPaymentDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? throw new InvalidOperationException("Set ConnectionStrings__DefaultConnection before running EF migrations.");
        return new OrderPaymentDbContext(new DbContextOptionsBuilder<OrderPaymentDbContext>()
            .UseSqlServer(connection).Options);
    }
}
