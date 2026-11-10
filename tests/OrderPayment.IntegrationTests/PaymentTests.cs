using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderPayment.Application.Contracts;
using OrderPayment.Application.Payments;
using OrderPayment.Domain;
using OrderPayment.Infrastructure.Persistence;
using Xunit;

namespace OrderPayment.IntegrationTests;

[Collection("SqlServer API")]
public sealed class PaymentTests(ApiFixture fixture)
{
    [Fact]
    public async Task Parallel_same_key_creates_one_payment_and_other_key_conflicts()
    {
        using var client = await fixture.AuthenticatedClient();
        var order = await CreateOrder(client);
        var key = Guid.NewGuid().ToString("N");
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => CreatePaymentRequest(client, order.Id, key)));
        foreach (var response in responses) response.EnsureSuccessStatusCode();
        var payments = await Task.WhenAll(responses.Select(Read<PaymentResponse>));
        payments.Select(x => x.Id).Distinct().Should().ContainSingle();
        (await CreatePaymentRequest(client, order.Id, "another-key")).StatusCode.Should().Be(HttpStatusCode.Conflict);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderPaymentDbContext>();
        (await db.Payments.CountAsync(x => x.OrderId == order.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Sequential_and_parallel_duplicate_callbacks_apply_order_and_outbox_once()
    {
        using var client = await fixture.AuthenticatedClient();
        var order = await CreateOrder(client);
        var payment = await Read<PaymentResponse>(await CreatePaymentRequest(client, order.Id, "callback-" + Guid.NewGuid().ToString("N")));
        var callback = new PaymentCallback("event-" + Guid.NewGuid().ToString("N"), payment.PaymentReference, PaymentCallbackStatus.Succeeded, payment.Amount);
        for (var i = 0; i < 3; i++) (await SendCallback(client, callback)).EnsureSuccessStatusCode();
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => SendCallback(client, callback)));
        foreach (var response in concurrent) response.EnsureSuccessStatusCode();

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderPaymentDbContext>();
        (await db.PaymentEvents.CountAsync(x => x.EventId == callback.EventId)).Should().Be(1);
        (await db.Payments.SingleAsync(x => x.Id == payment.Id)).Status.Should().Be(PaymentStatus.Succeeded);
        (await db.Orders.SingleAsync(x => x.Id == order.Id)).Status.Should().Be(OrderStatus.Paid);
        (await db.OutboxMessages.CountAsync(x => x.Type == "PaymentSucceeded" && x.Payload.Contains(payment.PaymentReference))).Should().Be(1);
    }

    [Fact]
    public async Task Invalid_signature_or_amount_cannot_modify_payment()
    {
        using var client = await fixture.AuthenticatedClient();
        var order = await CreateOrder(client);
        var payment = await Read<PaymentResponse>(await CreatePaymentRequest(client, order.Id, "mismatch-" + Guid.NewGuid().ToString("N")));
        var callback = new PaymentCallback("event-" + Guid.NewGuid().ToString("N"), payment.PaymentReference, PaymentCallbackStatus.Succeeded, payment.Amount);
        (await SendCallback(client, callback, "00")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SendCallback(client, callback with { Amount = payment.Amount + 0.01m })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderPaymentDbContext>();
        (await db.PaymentEvents.CountAsync(x => x.EventId == callback.EventId)).Should().Be(0);
        (await db.Payments.SingleAsync(x => x.Id == payment.Id)).Status.Should().Be(PaymentStatus.Pending);
    }

    [Fact]
    public async Task Callback_event_id_cannot_be_reused_with_changed_payload()
    {
        using var client = await fixture.AuthenticatedClient();
        var order = await CreateOrder(client);
        var payment = await Read<PaymentResponse>(await CreatePaymentRequest(client, order.Id, "event-conflict-" + Guid.NewGuid().ToString("N")));
        var callback = new PaymentCallback("event-" + Guid.NewGuid().ToString("N"), payment.PaymentReference, PaymentCallbackStatus.Succeeded, payment.Amount);
        (await SendCallback(client, callback)).EnsureSuccessStatusCode();
        (await SendCallback(client, callback with { Status = PaymentCallbackStatus.Failed })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Later_failed_event_does_not_reverse_paid_order()
    {
        using var client = await fixture.AuthenticatedClient();
        var order = await CreateOrder(client);
        var payment = await Read<PaymentResponse>(await CreatePaymentRequest(client, order.Id, "out-of-order-" + Guid.NewGuid().ToString("N")));
        (await SendCallback(client, new("success-" + Guid.NewGuid().ToString("N"), payment.PaymentReference, PaymentCallbackStatus.Succeeded, payment.Amount))).EnsureSuccessStatusCode();
        var late = await SendCallback(client, new("failure-" + Guid.NewGuid().ToString("N"), payment.PaymentReference, PaymentCallbackStatus.Failed, payment.Amount));
        (await Read<WebhookResult>(late)).Ignored.Should().BeTrue();
        var updated = await Read<OrderResponse>(await client.GetAsync($"/api/orders/{order.Id}"));
        updated.Status.Should().Be(OrderStatus.Paid);
    }

    [Fact]
    public async Task Success_after_cancellation_flags_review_without_resurrecting_order()
    {
        using var client = await fixture.AuthenticatedClient();
        var order = await CreateOrder(client);
        var payment = await Read<PaymentResponse>(await CreatePaymentRequest(client, order.Id, "cancel-late-" + Guid.NewGuid().ToString("N")));
        (await client.DeleteAsync($"/api/orders/{order.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var result = await Read<WebhookResult>(await SendCallback(client,
            new("late-" + Guid.NewGuid().ToString("N"), payment.PaymentReference, PaymentCallbackStatus.Succeeded, payment.Amount)));
        result.Ignored.Should().BeTrue();

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderPaymentDbContext>();
        (await db.Orders.SingleAsync(x => x.Id == order.Id)).Status.Should().Be(OrderStatus.Cancelled);
        (await db.Payments.SingleAsync(x => x.Id == payment.Id)).RequiresReview.Should().BeTrue();
    }

    [Fact]
    public async Task Paid_order_can_be_shipped_then_completed_without_manual_paid_override()
    {
        using var client = await fixture.AuthenticatedClient();
        var order = await CreateOrder(client);
        (await client.PatchAsJsonAsync($"/api/orders/{order.Id}/status", new { status = "Shipped" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PatchAsJsonAsync($"/api/orders/{order.Id}/status", new { status = "Paid" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payment = await Read<PaymentResponse>(await CreatePaymentRequest(client, order.Id, "ship-" + Guid.NewGuid().ToString("N")));
        (await SendCallback(client, new("ship-event-" + Guid.NewGuid().ToString("N"), payment.PaymentReference,
            PaymentCallbackStatus.Succeeded, payment.Amount))).EnsureSuccessStatusCode();
        var shipped = await Read<OrderResponse>(await client.PatchAsJsonAsync($"/api/orders/{order.Id}/status", new { status = "Shipped" }));
        shipped.Status.Should().Be(OrderStatus.Shipped);
        var completed = await Read<OrderResponse>(await client.PatchAsJsonAsync($"/api/orders/{order.Id}/status", new { status = "Completed" }));
        completed.Status.Should().Be(OrderStatus.Completed);
        (await client.PatchAsJsonAsync($"/api/orders/{order.Id}/status", new { status = "Shipped" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    private async Task<HttpResponseMessage> SendCallback(HttpClient client, PaymentCallback callback, string? signatureOverride = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(callback, ApiFixture.JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/payment")
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(fixture.WebhookSecret), body)).ToLowerInvariant();
        request.Headers.Add("X-Payment-Signature", signatureOverride ?? signature);
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> CreatePaymentRequest(HttpClient client, Guid orderId, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/orders/{orderId}/payments");
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    private static async Task<OrderResponse> CreateOrder(HttpClient client)
    {
        var product = await Read<ProductResponse>(await client.PostAsJsonAsync("/api/products",
            new ProductRequest("Payment product", "PAY-" + Guid.NewGuid().ToString("N"), 10.25m, 5)));
        var customer = await Read<CustomerResponse>(await client.PostAsJsonAsync("/api/customers",
            new CustomerRequest("Payment customer", $"{Guid.NewGuid():N}@example.test", null)));
        return await Read<OrderResponse>(await client.PostAsJsonAsync("/api/orders", new OrderRequest(customer.Id, [new(product.Id, 2)])));
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(ApiFixture.JsonOptions))!;
    }
}
