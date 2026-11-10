using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderPayment.Application.Abstractions;
using OrderPayment.Infrastructure.Persistence;
using OrderPayment.Infrastructure.Repositories;

namespace OrderPayment.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<OrderPaymentDbContext>(options => options.UseSqlServer(connectionString));
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IRevenueReportReader, RevenueReportReader>();
        return services;
    }
}
