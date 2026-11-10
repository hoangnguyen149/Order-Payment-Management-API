using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderPayment.Application.Payments;
using OrderPayment.Domain;
using OrderPayment.Infrastructure.Payments;
using Xunit;

namespace OrderPayment.UnitTests;

public sealed class GatewayClientTests
{
    [Fact]
    public async Task Transient_gateway_error_retries_with_same_reference_and_body()
    {
        var handler = new RecordingHandler((attempt, _) => attempt == 1
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : Json("{\"paymentReference\":\"reference-1\",\"amount\":10.25,\"status\":\"Pending\"}"));
        using var provider = Services(handler);

        var result = await provider.GetRequiredService<IPaymentGatewayClient>().CreateAsync("reference-1", 10.25m, CancellationToken.None);

        result.Status.Should().Be(PaymentStatus.Pending);
        handler.Keys.Should().Equal("reference-1", "reference-1");
        handler.Bodies.Should().HaveCount(2).And.OnlyContain(x => x == handler.Bodies[0]);
        handler.Bodies[0].Should().Contain("10.25");
    }

    [Fact]
    public async Task Gateway_bad_request_is_not_retried()
    {
        var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var provider = Services(handler);
        Func<Task> create = () => provider.GetRequiredService<IPaymentGatewayClient>().CreateAsync("reference-1", 10m, CancellationToken.None);

        (await create.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        handler.Bodies.Should().ContainSingle();
    }

    [Fact]
    public async Task Gateway_transient_errors_stop_after_three_attempts()
    {
        var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var provider = Services(handler);
        Func<Task> create = () => provider.GetRequiredService<IPaymentGatewayClient>().CreateAsync("reference-1", 10m, CancellationToken.None);

        await create.Should().ThrowAsync<HttpRequestException>();
        handler.Bodies.Should().HaveCount(3);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"paymentReference\":\"wrong\",\"amount\":10.25,\"status\":\"Pending\"}")]
    [InlineData("{\"paymentReference\":\"reference-1\",\"amount\":999,\"status\":\"Pending\"}")]
    [InlineData("{\"paymentReference\":\"reference-1\",\"amount\":10.25,\"status\":999}")]
    [InlineData("{\"paymentReference\":\"reference-1\",\"amount\":10.25}")]
    [InlineData("{\"amount\":10.25,\"status\":\"Pending\"}")]
    [InlineData("{\"paymentReference\":\"reference-1\",\"status\":\"Pending\"}")]
    [InlineData("{\"paymentReference\":\"reference-1\",\"amount\":10.251,\"status\":\"Pending\"}")]
    [InlineData("{\"paymentReference\":\"reference-1\",\"amount\":1000000000000001,\"status\":\"Pending\"}")]
    public async Task Invalid_gateway_response_is_rejected(string json)
    {
        using var handler = new RecordingHandler((_, _) => Json(json));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://gateway.test/") };
        Func<Task> create = () => new GatewayPaymentClient(http).CreateAsync("reference-1", 10.25m, CancellationToken.None);
        await create.Should().ThrowAsync<HttpRequestException>();
    }

    private static ServiceProvider Services(RecordingHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationManager { ["Gateway:BaseUrl"] = "https://gateway.test/" };
        services.AddGatewayPaymentClient(configuration);
        services.AddHttpClient(nameof(IPaymentGatewayClient)).ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(Func<int, HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<string> Keys { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Keys.Add(request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : "");
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return reply(Bodies.Count, request);
        }
    }
}
