using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using FakeHost = OrderPayment.FakeExternalApi.FakeExternalProgram;
using CallbackSender = OrderPayment.FakeExternalApi.CallbackSender;

namespace OrderPayment.IntegrationTests;

public sealed class SimulatorTests
{
    [Fact]
    public async Task Gateway_reference_is_idempotent_and_admin_routes_require_key()
    {
        await using var simulator = new Simulator();
        using var client = simulator.Factory.CreateClient();
        var first = await client.PostAsJsonAsync("/payments", new { paymentReference = "sim-reference", amount = 10.25m });
        first.EnsureSuccessStatusCode();
        var replay = await client.PostAsJsonAsync("/payments", new { paymentReference = "sim-reference", amount = 10.25m });
        replay.EnsureSuccessStatusCode();
        (await replay.Content.ReadAsStringAsync()).Should().Be(await first.Content.ReadAsStringAsync());
        (await client.PostAsJsonAsync("/payments", new { paymentReference = "sim-reference", amount = 11m }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsJsonAsync("/admin/payments/sim-reference/settle", new { status = "Succeeded" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Settlement_signs_identical_callbacks_and_survives_host_restart()
    {
        await using var simulator = new Simulator();
        string eventId;
        using (var client = simulator.AdminClient())
        {
            (await client.PostAsJsonAsync("/payments", new { paymentReference = "durable-reference", amount = 10.25m })).EnsureSuccessStatusCode();
            var settled = await client.PostAsJsonAsync("/admin/payments/durable-reference/settle", new { status = "Succeeded" });
            settled.EnsureSuccessStatusCode();
            var result = await settled.Content.ReadFromJsonAsync<JsonElement>();
            eventId = result.GetProperty("eventId").GetString()!;
            result.GetProperty("webhookDelivered").GetBoolean().Should().BeTrue();
            (await client.PostAsync("/admin/payments/durable-reference/callback?count=3", null)).EnsureSuccessStatusCode();
            (await client.PostAsJsonAsync("/admin/payments/durable-reference/settle", new { status = "Failed" }))
                .StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        simulator.Captured.Should().HaveCount(4);
        foreach (var callback in simulator.Captured)
        {
            Convert.FromHexString(callback.Signature).Should().Equal(HMACSHA256.HashData(Encoding.UTF8.GetBytes(simulator.WebhookSecret), callback.Body));
            JsonSerializer.Deserialize<JsonElement>(callback.Body).GetProperty("eventId").GetString().Should().Be(eventId);
        }
        await simulator.Restart();
        using var restarted = simulator.AdminClient();
        var payment = await restarted.GetFromJsonAsync<JsonElement>("/payments/durable-reference");
        payment.GetProperty("status").GetString().Should().Be("Succeeded");
        var replay = await restarted.PostAsJsonAsync("/admin/payments/durable-reference/settle", new { status = "Succeeded" });
        replay.EnsureSuccessStatusCode();
        (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eventId").GetString().Should().Be(eventId);
    }

    [Fact]
    public async Task Marketplace_paging_keeps_snapshot_and_rejects_tampered_cursor()
    {
        await using var simulator = new Simulator();
        using var client = simulator.AdminClient();
        for (var i = 1; i <= 3; i++) await Upsert(client, "market-" + i, i);
        var until = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O");
        var path = "/marketplace/orders?since=2020-01-01T00%3A00%3A00Z&until=" + Uri.EscapeDataString(until) + "&pageSize=1";
        var first = await client.GetFromJsonAsync<JsonElement>(path);
        var cursor = first.GetProperty("nextCursor").GetString()!;
        var ids = new List<string> { first.GetProperty("items")[0].GetProperty("externalId").GetString()! };
        await Upsert(client, "after-snapshot", 4);
        var next = cursor;
        while (next is not null)
        {
            var page = await client.GetFromJsonAsync<JsonElement>(path + "&cursor=" + Uri.EscapeDataString(next));
            ids.AddRange(page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("externalId").GetString()!));
            next = page.GetProperty("nextCursor").GetString();
        }
        ids.Should().BeEquivalentTo("market-1", "market-2", "market-3");
        (await client.GetAsync(path + "&cursor=" + Uri.EscapeDataString(cursor + "x"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var changedBounds = path.Replace("since=2020", "since=2021", StringComparison.Ordinal);
        (await client.GetAsync(changedBounds + "&cursor=" + Uri.EscapeDataString(cursor))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await Upsert(client, "market-1", 9);
        var latest = await client.GetFromJsonAsync<JsonElement>(path.Replace("pageSize=1", "pageSize=100", StringComparison.Ordinal));
        var revisions = latest.GetProperty("items").EnumerateArray().Where(x => x.GetProperty("externalId").GetString() == "market-1");
        revisions.Select(x => x.GetProperty("items")[0].GetProperty("quantity").GetInt32()).Should().Equal(1, 9);
    }

    private static async Task Upsert(HttpClient client, string id, int quantity)
    {
        var response = await client.PostAsJsonAsync("/admin/marketplace/orders", new
        {
            externalId = id, customerEmail = "market@example.test", customerName = "Market buyer",
            items = new[] { new { sku = "SKU-1", quantity } }
        });
        response.EnsureSuccessStatusCode();
    }

    private sealed class Simulator : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "orderpayment-simulator-tests", Guid.NewGuid().ToString("N"));
        private readonly string _adminKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        public string WebhookSecret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        public ConcurrentQueue<Capture> Captured { get; } = new();
        public WebApplicationFactory<FakeHost> Factory { get; private set; }

        public Simulator()
        {
            Directory.CreateDirectory(_directory);
            Factory = CreateFactory();
        }

        public HttpClient AdminClient()
        {
            var client = Factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Simulator-Key", _adminKey);
            return client;
        }

        public async Task Restart()
        {
            await Factory.DisposeAsync();
            Factory = CreateFactory();
        }

        private WebApplicationFactory<FakeHost> CreateFactory()
        {
            var settings = new Dictionary<string, string?>
            {
                ["Fake:AdminKey"] = _adminKey,
                ["Fake:DatabasePath"] = Path.Combine(_directory, "simulator.db"),
                ["Fake:ApiBaseUrl"] = "http://callback.test/",
                ["Payments:WebhookSecret"] = WebhookSecret
            };
            return new WebApplicationFactory<FakeHost>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                foreach (var pair in settings) builder.UseSetting(pair.Key, pair.Value);
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
                builder.ConfigureServices(services => services.AddHttpClient<CallbackSender>()
                    .ConfigurePrimaryHttpMessageHandler(() => new CaptureHandler(Captured)));
            });
        }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed record Capture(byte[] Body, string Signature);
    private sealed class CaptureHandler(ConcurrentQueue<Capture> captured) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            captured.Enqueue(new(await request.Content!.ReadAsByteArrayAsync(ct), request.Headers.GetValues("X-Payment-Signature").Single()));
            return new(HttpStatusCode.OK);
        }
    }
}
