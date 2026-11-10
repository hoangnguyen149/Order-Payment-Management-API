using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderPayment.Application.Payments;
using OrderPayment.Application.Orders;
using OrderPayment.Infrastructure.Orders;

namespace OrderPayment.Infrastructure.Payments;

public static class PaymentInfrastructureExtensions
{
    public static IServiceCollection AddPaymentInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddGatewayPaymentClient(configuration);
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<IOrderStatusService, OrderStatusService>();
        return services;
    }
}
