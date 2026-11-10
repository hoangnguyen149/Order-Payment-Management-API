using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderPayment.Infrastructure.Persistence;
using OrderPayment.Application.Payments;
using OrderPayment.Domain;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Collections.Concurrent;
using OrderPayment.Application.Marketplace;
using OrderPayment.Infrastructure.Jobs;
using Testcontainers.MsSql;
using Xunit;

namespace OrderPayment.IntegrationTests;

public sealed class ApiFixture : IAsyncLifetime
{
    public string WebhookSecret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public FakeGateway Gateway { get; } = new();
    public FakeMarketplace Marketplace { get; } = new();
    public TestOutboxSink OutboxSink { get; } = new();
    private readonly string _adminPassword = "Test-a1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
    private readonly string _staffPassword = "Test-b2!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
    private readonly Dictionary<string, string> _tokens = [];
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private readonly MsSqlContainer _sql = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .WithPassword("Test-sql3!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20)))
        .Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task InitializeAsync()
    {
        await _sql.StartAsync();
        var connection = new SqlConnectionStringBuilder(_sql.GetConnectionString()) { InitialCatalog = "OrderPaymentTests" };
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = connection.ConnectionString,
            ["Jwt:SigningKey"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            ["Jwt:Issuer"] = "IntegrationTests",
            ["Jwt:Audience"] = "IntegrationTests.Client",
            ["Auth:AdminUsername"] = "admin",
            ["Auth:AdminPassword"] = _adminPassword,
            ["Auth:StaffUsername"] = "staff",
            ["Auth:StaffPassword"] = _staffPassword,
            ["Payments:WebhookSecret"] = WebhookSecret,
            ["Jobs:Enabled"] = "false",
            ["Gateway:BaseUrl"] = "http://localhost:8081/",
            ["Marketplace:BaseUrl"] = "http://localhost:8081/",
            ["RateLimiting:GlobalPermitLimit"] = "10000",
            ["Database:MigrateOnStartup"] = "false"
        };
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            foreach (var pair in settings) builder.UseSetting(pair.Key, pair.Value);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPaymentGatewayClient>();
                services.AddSingleton<IPaymentGatewayClient>(Gateway);
                services.RemoveAll<IMarketplaceClient>();
                services.AddSingleton<IMarketplaceClient>(Marketplace);
                services.RemoveAll<IOutboxSink>();
                services.AddSingleton<IOutboxSink>(OutboxSink);
            });
        });

        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OrderPaymentDbContext>().Database.MigrateAsync();
    }

    public HttpClient AnonymousClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        BaseAddress = new Uri("http://localhost")
    });

    public async Task<HttpClient> AuthenticatedClient(string role = "Admin")
    {
        var client = AnonymousClient();
        await _loginLock.WaitAsync();
        try
        {
            if (!_tokens.TryGetValue(role, out var token))
            {
                var response = await client.PostAsJsonAsync("/api/auth/login", new
                {
                    username = role == "Admin" ? "admin" : "staff",
                    password = role == "Admin" ? _adminPassword : _staffPassword
                });
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                token = body.GetProperty("accessToken").GetString()!;
                _tokens.Add(role, token);
            }
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        finally { _loginLock.Release(); }
        return client;
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null) await Factory.DisposeAsync();
        await _sql.DisposeAsync();
        _loginLock.Dispose();
    }
}

public sealed class FakeGateway : IPaymentGatewayClient
{
    private readonly ConcurrentDictionary<string, GatewayPaymentResponse> _payments = new();
    public Task<GatewayPaymentResponse> CreateAsync(string paymentReference, decimal amount, CancellationToken ct) =>
        Task.FromResult(_payments.GetOrAdd(paymentReference, _ => new(paymentReference, PaymentStatus.Pending, amount)));
    public Task<GatewayPaymentResponse> GetAsync(string paymentReference, CancellationToken ct) =>
        Task.FromResult(_payments[paymentReference]);
    public void SetStatus(string paymentReference, PaymentStatus status) =>
        _payments.AddOrUpdate(paymentReference, _ => throw new InvalidOperationException("Unknown test payment."),
            (_, previous) => previous with { Status = status });
}

public sealed class FakeMarketplace : IMarketplaceClient
{
    public IReadOnlyList<MarketplaceOrder> Orders { get; set; } = [];
    public DateTimeOffset LastSince { get; private set; }
    public Task<MarketplacePage> GetOrdersAsync(DateTimeOffset since, DateTimeOffset until, string? cursor, CancellationToken ct)
    {
        LastSince = since;
        return Task.FromResult(new MarketplacePage(Orders.Where(x => x.UpdatedAt >= since && x.UpdatedAt <= until).ToArray(), null));
    }
}

public sealed class TestOutboxSink : IOutboxSink
{
    public Guid? FailMessage { get; set; }
    public ConcurrentDictionary<Guid, int> Calls { get; } = new();
    public Task DeliverAsync(Guid messageId, string eventType, CancellationToken ct)
    {
        Calls.AddOrUpdate(messageId, 1, (_, count) => count + 1);
        if (FailMessage == messageId) throw new InvalidOperationException("Simulated test failure.");
        return Task.CompletedTask;
    }
}

[CollectionDefinition("SqlServer API", DisableParallelization = true)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>;
