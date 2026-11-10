using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderPayment.Application.Marketplace;

namespace OrderPayment.Infrastructure.Jobs;

public static class JobServiceCollectionExtensions
{
    public static IServiceCollection AddJobInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JobOptions>().Bind(configuration.GetSection("Jobs"))
            .Validate(o => o.PollSeconds is >= 1 and <= 3600 && o.PendingPaymentMinutes is >= 1 and <= 1440
                && o.MarketplaceIntervalSeconds is >= 1 and <= 86400, "Job polling intervals are outside supported bounds.")
            .ValidateOnStart();
        services.AddOptions<MarketplaceOptions>().Bind(configuration.GetSection("Marketplace"))
            .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var url) && url.Scheme is "http" or "https",
                "Marketplace:BaseUrl must be an absolute HTTP(S) URI.").ValidateOnStart();
        services.AddHttpClient<IMarketplaceClient, MarketplaceClient>((provider, http) =>
        {
            var options = provider.GetRequiredService<IOptions<MarketplaceOptions>>().Value;
            http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddSingleton<IOutboxSink, LoggingOutboxSink>();
        services.AddScoped<OutboxProcessor>();
        services.AddScoped<PaymentReconciler>();
        services.AddScoped<MarketplaceSyncProcessor>();
        services.AddHostedService<OutboxWorker>();
        services.AddHostedService<ReconciliationWorker>();
        services.AddHostedService<MarketplaceWorker>();
        return services;
    }
}

internal abstract class PeriodicJobWorker<TProcessor>(IServiceScopeFactory scopes, IOptions<JobOptions> options,
    ILogger logger) : BackgroundService where TProcessor : notnull
{
    protected abstract int IntervalSeconds(JobOptions options);
    protected abstract Task RunAsync(TProcessor processor, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(IntervalSeconds(options.Value)));
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await RunAsync(scope.ServiceProvider.GetRequiredService<TProcessor>(), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    logger.LogWarning("Job {JobType} iteration failed with {ErrorType}", typeof(TProcessor).Name, error.GetType().Name);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

internal sealed class OutboxWorker(IServiceScopeFactory scopes, IOptions<JobOptions> options, ILogger<OutboxWorker> logger)
    : PeriodicJobWorker<OutboxProcessor>(scopes, options, logger)
{
    protected override int IntervalSeconds(JobOptions options) => options.PollSeconds;
    protected override Task RunAsync(OutboxProcessor processor, CancellationToken ct) => processor.RunOnceAsync(ct);
}

internal sealed class ReconciliationWorker(IServiceScopeFactory scopes, IOptions<JobOptions> options, ILogger<ReconciliationWorker> logger)
    : PeriodicJobWorker<PaymentReconciler>(scopes, options, logger)
{
    protected override int IntervalSeconds(JobOptions options) => options.PollSeconds;
    protected override Task RunAsync(PaymentReconciler processor, CancellationToken ct) => processor.RunOnceAsync(ct);
}

internal sealed class MarketplaceWorker(IServiceScopeFactory scopes, IOptions<JobOptions> options, ILogger<MarketplaceWorker> logger)
    : PeriodicJobWorker<MarketplaceSyncProcessor>(scopes, options, logger)
{
    protected override int IntervalSeconds(JobOptions options) => options.MarketplaceIntervalSeconds;
    protected override Task RunAsync(MarketplaceSyncProcessor processor, CancellationToken ct) => processor.RunOnceAsync(ct);
}
