using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace OrderPayment.IntegrationTests;

// Kiểm tra HTTP/JWT thật; không gọi SQL. Các test ApiTests riêng vẫn bắt buộc SQL Server.
public sealed class HttpPipelineFixture : IDisposable
{
    private readonly Dictionary<string, string> _tokens = [];
    public string WebhookSecret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string AdminPassword { get; } = "Http-a1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
    private string StaffPassword { get; } = "Http-b2!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
    public WebApplicationFactory<Program> Factory { get; }

    public HttpPipelineFixture()
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Server=localhost;Database=NotUsedByHttpTests;Integrated Security=true;TrustServerCertificate=true",
            ["Jwt:SigningKey"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            ["Jwt:Issuer"] = "HttpTests",
            ["Jwt:Audience"] = "HttpTests.Client",
            ["Auth:AdminUsername"] = "admin",
            ["Auth:AdminPassword"] = AdminPassword,
            ["Auth:StaffUsername"] = "staff",
            ["Auth:StaffPassword"] = StaffPassword,
            ["Payments:WebhookSecret"] = WebhookSecret,
            ["Jobs:Enabled"] = "false",
            ["Gateway:BaseUrl"] = "http://localhost:8081/",
            ["Marketplace:BaseUrl"] = "http://localhost:8081/",
            ["Database:MigrateOnStartup"] = "false"
        };
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            foreach (var pair in settings) builder.UseSetting(pair.Key, pair.Value);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        });
    }

    public HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public Task<HttpClient> StaffClient() => AuthenticatedClient("staff", StaffPassword);
    public Task<HttpClient> AdminClient() => AuthenticatedClient("admin", AdminPassword);

    private async Task<HttpClient> AuthenticatedClient(string username, string password)
    {
        var client = Client();
        if (!_tokens.TryGetValue(username, out var token))
        {
            var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            token = body.GetProperty("accessToken").GetString()!;
            _tokens.Add(username, token);
        }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public void Dispose() => Factory.Dispose();
}

public sealed class HttpPipelineTests(HttpPipelineFixture fixture) : IClassFixture<HttpPipelineFixture>
{
    [Fact]
    public async Task Login_issues_signed_JWT_and_rejects_invalid_password()
    {
        using var client = fixture.Client();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = fixture.AdminPassword });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = await response.Content.ReadFromJsonAsync<JsonElement>();
        token.GetProperty("role").GetString().Should().Be("Admin");
        token.GetProperty("expiresAt").GetDateTimeOffset().Should().BeAfter(DateTimeOffset.UtcNow);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetProperty("accessToken").GetString());
        // Query sai phải đến được validator phía sau JWT, không phải bị 401.
        var invalidQuery = await client.GetAsync("/api/products?page=0");
        invalidQuery.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertProblem(invalidQuery, 400);

        var failed = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "wrong" });
        failed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertProblem(failed, 401);
    }

    [Fact]
    public async Task Anonymous_and_staff_permissions_return_problem_details()
    {
        using var anonymous = fixture.Client();
        var unauthorized = await anonymous.GetAsync("/api/products");
        unauthorized.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertProblem(unauthorized, 401);

        using var staff = await fixture.StaffClient();
        var forbidden = await staff.PostAsJsonAsync("/api/products", new { name = "A", sku = "A", price = 10, stockQuantity = 1 });
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertProblem(forbidden, 403);
    }

    [Fact]
    public async Task Malformed_JSON_and_invalid_DTO_return_400_without_querying_SQL()
    {
        using var staff = await fixture.StaffClient();
        var malformed = await staff.PostAsync("/api/orders", new StringContent("{", System.Text.Encoding.UTF8, "application/json"));
        malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertProblem(malformed, 400);

        var invalid = await staff.PostAsJsonAsync("/api/orders", new { customerId = Guid.NewGuid(), items = new[] { new { productId = Guid.NewGuid(), quantity = 0 } } });
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertProblem(invalid, 400);
        var json = await invalid.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(json);
        body.TryGetProperty("errors", out var errors).Should().BeTrue(json);
        errors.EnumerateObject().Should().Contain(x => x.Name.Contains("Quantity"));
    }

    [Fact]
    public async Task Invalid_JWT_is_rejected()
    {
        using var client = fixture.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-signed-token");
        var response = await client.GetAsync("/api/orders");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertProblem(response, 401);
    }

    [Fact]
    public async Task Report_query_validation_rejects_invalid_range_before_reading_SQL()
    {
        using var admin = await fixture.AdminClient();
        var response = await admin.GetAsync("/api/reports/revenue/daily?from=2025-02-01&toExclusive=2025-01-01");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertProblem(response, 400);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("errors").EnumerateObject().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Staff_cannot_access_revenue_report()
    {
        using var staff = await fixture.StaffClient();
        var response = await staff.GetAsync("/api/reports/revenue/daily?from=2025-01-01&toExclusive=2025-02-01");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertProblem(response, 403);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("bad key")]
    [InlineData("bad/key")]
    public async Task Payment_creation_requires_valid_idempotency_key_before_querying_SQL(string? key)
    {
        using var client = await fixture.StaffClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/orders/{Guid.NewGuid()}/payments");
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertProblem(response, 400);
    }

    [Fact]
    public async Task Unsigned_webhook_is_rejected_before_querying_SQL()
    {
        using var client = fixture.Client();
        var response = await client.PostAsync("/api/webhooks/payment", new StringContent("{}", Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertProblem(response, 401);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"eventId\":\"evt-1\",\"paymentReference\":\"ref-1\",\"amount\":10}")]
    [InlineData("{\"eventId\":\"evt-1\",\"paymentReference\":\"ref-1\",\"status\":999,\"amount\":10}")]
    [InlineData("{\"eventId\":\"evt-1\",\"paymentReference\":\"ref-1\",\"status\":\"Succeeded\",\"amount\":0}")]
    public async Task Signed_invalid_webhook_is_rejected_before_querying_SQL(string json)
    {
        using var client = fixture.Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/payment")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Payment-Signature", Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(fixture.WebhookSecret), Encoding.UTF8.GetBytes(json))).ToLowerInvariant());
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertProblem(response, 400);
    }

    [Fact]
    public async Task Oversized_webhook_is_rejected()
    {
        using var client = fixture.Client();
        var response = await client.PostAsync("/api/webhooks/payment", new StringContent(new string('x', 65537), Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Swagger_and_liveness_are_available_and_unknown_route_returns_problem_details()
    {
        using var client = fixture.Client();
        var swagger = await client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");
        swagger.GetProperty("paths").TryGetProperty("/api/orders", out _).Should().BeTrue();
        swagger.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("Bearer", out _).Should().BeTrue();
        (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        var missing = await client.GetAsync("/route-that-does-not-exist");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await AssertProblem(missing, 404);
    }

    private static async Task AssertProblem(HttpResponseMessage response, int status)
    {
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("status").GetInt32().Should().Be(status);
        body.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
        response.Headers.Contains("X-Correlation-ID").Should().BeTrue();
    }
}
