using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using OrderPayment.Application.Abstractions;
using OrderPayment.Application.Services;

namespace OrderPayment.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<DependencyInjectionMarker>();
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<ICustomerService, CustomerService>();
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<IRevenueReportService, RevenueReportService>();
        return services;
    }
    private sealed class DependencyInjectionMarker;
}
