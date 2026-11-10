using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OrderPayment.Application.Payments;
using Polly;

namespace OrderPayment.Infrastructure.Payments;

public sealed class GatewayOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string? SimulationMode { get; set; }

    internal static bool IsValidBaseUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
}

public static class GatewayServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayPaymentClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<GatewayOptions>()
            .Configure(options =>
            {
                options.BaseUrl = configuration["Gateway:BaseUrl"] ?? string.Empty;
                options.SimulationMode = configuration["Gateway:SimulationMode"];
            })
            .Validate(options => GatewayOptions.IsValidBaseUrl(options.BaseUrl),
                "Gateway:BaseUrl must be an absolute HTTP(S) URL without credentials, query or fragment.")
            .Validate(options => string.IsNullOrEmpty(options.SimulationMode) ||
                options.SimulationMode is "flaky" or "delay" or "timeout" or "always500",
                "Gateway:SimulationMode must be empty, flaky, delay, timeout or always500.")
            .ValidateOnStart();

        services.AddHttpClient<IPaymentGatewayClient, GatewayPaymentClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<GatewayOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
            if (!string.IsNullOrEmpty(options.SimulationMode))
                client.DefaultRequestHeaders.Add("X-Fake-Mode", options.SimulationMode);
            // Giới hạn thời gian nằm trong resilience pipeline, tránh hai timeout cạnh tranh nhau.
            client.Timeout = Timeout.InfiniteTimeSpan;
        }).AddStandardResilienceHandler(options =>
        {
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(10);
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(2);
            options.Retry.MaxRetryAttempts = 2;
            options.Retry.Delay = TimeSpan.FromMilliseconds(250);
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            // Default handler retry transport/timeout và HTTP 408, 429, 5xx; không retry các 4xx khác.
        });
        return services;
    }
}
