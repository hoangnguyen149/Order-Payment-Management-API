using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using OrderPayment.Application.Contracts;
using OrderPayment.Domain;
using Xunit;

namespace OrderPayment.IntegrationTests;

[Collection("SqlServer API")]
public sealed class ApiTests(ApiFixture fixture)
{
    [Fact]
    public async Task Authentication_and_roles_protect_catalog_writes()
    {
        using var anonymous = fixture.AnonymousClient();
        (await anonymous.GetAsync("/api/products")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "incorrect" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var staff = await fixture.AuthenticatedClient("Staff");
        (await staff.GetAsync("/api/products")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await staff.PostAsJsonAsync("/api/products", Product(10m, 1)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Catalog_CRUD_lists_with_paging_and_returns_not_found_after_delete()
    {
        using var client = await fixture.AuthenticatedClient();
        var sku = "SKU-" + Guid.NewGuid().ToString("N");
        var product = await Create<ProductResponse>(client, "/api/products", new ProductRequest(sku, sku, 10.25m, 8));
        var updatedResponse = await client.PutAsJsonAsync($"/api/products/{product.Id}", new ProductRequest(sku, sku, 12.50m, 9));
        updatedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Read<ProductResponse>(updatedResponse)).Price.Should().Be(12.50m);

        var page = await client.GetFromJsonAsync<PageResult<ProductResponse>>($"/api/products?search={sku}&page=1&pageSize=1&sort=name");
        page!.TotalCount.Should().Be(1);
        page.Items.Should().ContainSingle(x => x.Id == product.Id);
        (await client.DeleteAsync($"/api/products/{product.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync($"/api/products/{product.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var customer = await CreateCustomer(client);
        var updatedCustomer = await client.PutAsJsonAsync($"/api/customers/{customer.Id}", new CustomerRequest("Updated", customer.Email, "+84912345678"));
        updatedCustomer.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Read<CustomerResponse>(updatedCustomer)).FullName.Should().Be("Updated");
        (await client.DeleteAsync($"/api/customers/{customer.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync($"/api/customers/{customer.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Order_uses_server_price_reserves_stock_updates_and_cancels_once()
    {
        using var client = await fixture.AuthenticatedClient();
        var customer = await CreateCustomer(client);
        var product = await Create<ProductResponse>(client, "/api/products", Product(19.99m, 10));
        // Những trường tiền giả từ client không tham gia tính tiền.
        var order = await Create<OrderResponse>(client, "/api/orders", new
        {
            customerId = customer.Id,
            totalAmount = 0,
            items = new[] { new { productId = product.Id, quantity = 3, unitPrice = 0 } }
        });
        order.Status.Should().Be(OrderStatus.Pending);
        order.TotalAmount.Should().Be(59.97m);
        order.Items.Should().ContainSingle(x => x.UnitPrice == 19.99m && x.Quantity == 3);
        (await client.GetFromJsonAsync<ProductResponse>($"/api/products/{product.Id}"))!.StockQuantity.Should().Be(7);

        var update = await client.PutAsJsonAsync($"/api/orders/{order.Id}", new OrderRequest(customer.Id, [new(product.Id, 5)]));
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Read<OrderResponse>(update)).TotalAmount.Should().Be(99.95m);
        (await client.GetFromJsonAsync<ProductResponse>($"/api/products/{product.Id}"))!.StockQuantity.Should().Be(5);

        (await client.DeleteAsync($"/api/orders/{order.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.DeleteAsync($"/api/orders/{order.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var cancelled = await Read<OrderResponse>(await client.GetAsync($"/api/orders/{order.Id}"));
        cancelled.Status.Should().Be(OrderStatus.Cancelled);
        (await client.GetFromJsonAsync<ProductResponse>($"/api/products/{product.Id}"))!.StockQuantity.Should().Be(10);
        (await client.PutAsJsonAsync($"/api/orders/{order.Id}", new OrderRequest(customer.Id, [new(product.Id, 1)])))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Validation_returns_problem_details_and_rejects_duplicate_SKU()
    {
        using var client = await fixture.AuthenticatedClient();
        var invalid = await client.PostAsJsonAsync("/api/products", new ProductRequest("", "", -1m, -1));
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        invalid.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(400);
        problem.GetProperty("errors").EnumerateObject().Should().NotBeEmpty();

        var request = Product(5m, 1);
        await Create<ProductResponse>(client, "/api/products", request);
        (await client.PostAsJsonAsync("/api/products", request)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.GetAsync("/api/products?page=0&pageSize=100000")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/orders", new { customerId = Guid.NewGuid(), items = (object?)null }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Failed_order_rolls_back_all_reserved_products_and_preserves_history()
    {
        using var client = await fixture.AuthenticatedClient();
        var customer = await CreateCustomer(client);
        var available = await Create<ProductResponse>(client, "/api/products", Product(4m, 5));
        var empty = await Create<ProductResponse>(client, "/api/products", Product(2m, 0));
        var response = await client.PostAsJsonAsync("/api/orders", new OrderRequest(customer.Id,
            [new(available.Id, 3), new(empty.Id, 1)]));
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.GetFromJsonAsync<ProductResponse>($"/api/products/{available.Id}"))!.StockQuantity.Should().Be(5);
        var orders = await Read<PageResult<OrderResponse>>(await client.GetAsync($"/api/orders?customerId={customer.Id}"));
        orders.TotalCount.Should().Be(0);

        var order = await Create<OrderResponse>(client, "/api/orders", new OrderRequest(customer.Id, [new(available.Id, 1)]));
        (await client.DeleteAsync($"/api/products/{available.Id}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.DeleteAsync($"/api/customers/{customer.Id}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.DeleteAsync($"/api/orders/{order.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Parallel_buyers_cannot_both_purchase_last_item()
    {
        using var client = await fixture.AuthenticatedClient();
        var customer = await CreateCustomer(client);
        var product = await Create<ProductResponse>(client, "/api/products", Product(10m, 1));
        var request = new OrderRequest(customer.Id, [new(product.Id, 1)]);

        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => client.PostAsJsonAsync("/api/orders", request)));

        responses.Select(x => x.StatusCode).Should().BeEquivalentTo([HttpStatusCode.Created, HttpStatusCode.Conflict]);
        (await client.GetFromJsonAsync<ProductResponse>($"/api/products/{product.Id}"))!.StockQuantity.Should().Be(0);
        var orders = await Read<PageResult<OrderResponse>>(await client.GetAsync($"/api/orders?customerId={customer.Id}"));
        orders.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task Parallel_cancellation_restocks_exactly_once()
    {
        using var client = await fixture.AuthenticatedClient();
        var customer = await CreateCustomer(client);
        var product = await Create<ProductResponse>(client, "/api/products", Product(10m, 3));
        var order = await Create<OrderResponse>(client, "/api/orders", new OrderRequest(customer.Id, [new(product.Id, 2)]));

        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => client.DeleteAsync($"/api/orders/{order.Id}")));

        responses.Select(x => x.StatusCode).Should().Contain(HttpStatusCode.NoContent)
            .And.OnlyContain(x => x == HttpStatusCode.NoContent || x == HttpStatusCode.Conflict);
        (await client.GetFromJsonAsync<ProductResponse>($"/api/products/{product.Id}"))!.StockQuantity.Should().Be(3);
    }

    private static ProductRequest Product(decimal price, int stock) => new("Product " + Guid.NewGuid().ToString("N"), "SKU-" + Guid.NewGuid().ToString("N"), price, stock);

    private static Task<CustomerResponse> CreateCustomer(HttpClient client) =>
        Create<CustomerResponse>(client, "/api/customers", new CustomerRequest("Test Customer", $"{Guid.NewGuid():N}@example.test", null));

    private static async Task<T> Create<T>(HttpClient client, string path, object request)
    {
        var response = await client.PostAsJsonAsync(path, request);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        response.Headers.Location.Should().NotBeNull();
        return await Read<T>(response);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(ApiFixture.JsonOptions))!;
    }
}
