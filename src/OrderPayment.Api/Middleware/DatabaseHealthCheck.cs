using Microsoft.Extensions.Diagnostics.HealthChecks;
using OrderPayment.Infrastructure.Persistence;

namespace OrderPayment.Api.Middleware;

public sealed class DatabaseHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderPaymentDbContext>();
        return await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Database unavailable.");
    }
}
