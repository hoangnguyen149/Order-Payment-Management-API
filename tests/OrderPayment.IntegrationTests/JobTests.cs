using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderPayment.Application.Marketplace;
using OrderPayment.Domain;
using OrderPayment.Infrastructure.Jobs;
using OrderPayment.Infrastructure.Persistence;
using Xunit;

namespace OrderPayment.IntegrationTests;

[Collection("SqlServer API")]
public sealed class JobTests(ApiFixture fixture)
{
    [Fact]
    public async Task Outbox_failure_is_durable_and_retry_delivers_then_marks_processed_once()
    {
        var message = new OutboxMessage("TestEvent", "{}");
        await WithDb(async db =>
        {
            db.OutboxMessages.Add(message);
            db.Entry(message).Property(x => x.CreatedAt).CurrentValue = DateTimeOffset.UtcNow.AddDays(-10);
            await db.SaveChangesAsync();
        });
        fixture.OutboxSink.FailMessage = message.Id;
        await Run<OutboxProcessor>(x => x.RunOnceAsync(CancellationToken.None));
        await WithDb(async db =>
        {
            var saved = await db.OutboxMessages.SingleAsync(x => x.Id == message.Id);
            saved.Attempts.Should().Be(1);
            saved.ProcessedAt.Should().BeNull();
            saved.NextAttemptAt.Should().BeAfter(DateTimeOffset.UtcNow);
            db.Entry(saved).Property(x => x.NextAttemptAt).CurrentValue = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        });
        fixture.OutboxSink.FailMessage = null;
        await Run<OutboxProcessor>(x => x.RunOnceAsync(CancellationToken.None));
        await Run<OutboxProcessor>(x => x.RunOnceAsync(CancellationToken.None));
        await WithDb(async db =>
        {
            (await db.OutboxMessages.SingleAsync(x => x.Id == message.Id)).ProcessedAt.Should().NotBeNull();
            (await db.OutboxDeliveries.CountAsync(x => x.MessageId == message.Id)).Should().Be(1);
        });
        fixture.OutboxSink.Calls[message.Id].Should().Be(2);
    }

    [Fact]
    public async Task Existing_consumer_receipt_prevents_repeated_delivery()
    {
        var message = new OutboxMessage("TestReceipt", "{}");
        await WithDb(async db =>
        {
            db.OutboxMessages.Add(message);
            db.OutboxDeliveries.Add(new OutboxDelivery(message.Id));
            db.Entry(message).Property(x => x.CreatedAt).CurrentValue = DateTimeOffset.UtcNow.AddDays(-10);
            await db.SaveChangesAsync();
        });
        await Run<OutboxProcessor>(x => x.RunOnceAsync(CancellationToken.None));
        fixture.OutboxSink.Calls.Should().NotContainKey(message.Id);
        await WithDb(async db => (await db.OutboxMessages.SingleAsync(x => x.Id == message.Id)).ProcessedAt.Should().NotBeNull());
    }

    [Fact]
    public async Task Reconciliation_recovers_missing_callback_for_aged_pending_payment()
    {
        Guid paymentId = default;
        Guid orderId = default;
        await WithDb(async db =>
        {
            var customer = new Customer("Reconcile", $"{Guid.NewGuid():N}@example.test", null);
            var product = new Product("Reconcile", "RECON-" + Guid.NewGuid().ToString("N"), 10m, 1);
            var order = new Order(customer.Id, [new OrderItem(product.Id, product.Name, product.Price, 1)]);
            product.Reserve(1);
            var payment = new Payment(order.Id, "reconcile-test", order.TotalAmount);
            db.Customers.Add(customer);
            db.Products.Add(product);
            db.Orders.Add(order);
            db.Payments.Add(payment);
            db.Entry(payment).Property(x => x.CreatedAt).CurrentValue = DateTimeOffset.UtcNow.AddMinutes(-60);
            await db.SaveChangesAsync();
            await fixture.Gateway.CreateAsync(payment.PaymentReference, payment.Amount, CancellationToken.None);
            fixture.Gateway.SetStatus(payment.PaymentReference, PaymentStatus.Succeeded);
            paymentId = payment.Id;
            orderId = order.Id;
        });
        await Run<PaymentReconciler>(x => x.RunOnceAsync(CancellationToken.None));
        await Run<PaymentReconciler>(x => x.RunOnceAsync(CancellationToken.None));
        await WithDb(async db =>
        {
            var saved = await db.Payments.SingleAsync(x => x.Id == paymentId);
            saved.Status.Should().Be(PaymentStatus.Succeeded);
            (await db.Orders.SingleAsync(x => x.Id == orderId)).Status.Should().Be(OrderStatus.Paid);
            (await db.PaymentEvents.CountAsync(x => x.PaymentReference == saved.PaymentReference)).Should().Be(1);
        });
    }

    [Fact]
    public async Task Marketplace_failure_replays_after_fix_and_durable_checkpoint_survives_new_scope()
    {
        var sku = "SYNC-" + Guid.NewGuid().ToString("N");
        var external = "external-" + Guid.NewGuid().ToString("N");
        var input = new MarketplaceOrder(external, DateTimeOffset.UtcNow.AddMinutes(-1),
            $"{Guid.NewGuid():N}@example.test", "Marketplace customer", [new(sku, 2)]);
        // Checkpoint cũ được đặt rõ ràng để test độc lập với thứ tự chạy các test SQL khác.
        await WithDb(async db =>
        {
            var checkpoint = await db.SyncCheckpoints.SingleOrDefaultAsync(x => x.Name == "mock-marketplace");
            if (checkpoint is not null)
            {
                db.Entry(checkpoint).Property(x => x.LastSyncedAtUtc).CurrentValue = DateTimeOffset.UnixEpoch;
                await db.SaveChangesAsync();
            }
        });
        fixture.Marketplace.Orders = [input];
        await Run<MarketplaceSyncProcessor>(x => x.RunOnceAsync(CancellationToken.None));
        await WithDb(async db =>
        {
            var failure = await db.SyncFailures.SingleAsync(x => x.ExternalId == external);
            failure.ResolvedAt.Should().BeNull();
            db.Entry(failure).Property(x => x.NextAttemptAt).CurrentValue = DateTimeOffset.UtcNow.AddSeconds(-1);
            db.Products.Add(new Product("Imported product", sku, 12.50m, 5));
            await db.SaveChangesAsync();
        });
        fixture.Marketplace.Orders = [];
        await Run<MarketplaceSyncProcessor>(x => x.RunOnceAsync(CancellationToken.None));
        DateTimeOffset checkpointAt = default;
        await WithDb(async db =>
        {
            (await db.SyncFailures.SingleAsync(x => x.ExternalId == external)).ResolvedAt.Should().NotBeNull();
            (await db.Orders.CountAsync(x => x.ExternalMarketplaceId == external)).Should().Be(1);
            (await db.Products.SingleAsync(x => x.Sku == sku)).StockQuantity.Should().Be(3);
            checkpointAt = (await db.SyncCheckpoints.SingleAsync(x => x.Name == "mock-marketplace")).LastSyncedAtUtc;
        });
        await Run<MarketplaceSyncProcessor>(x => x.RunOnceAsync(CancellationToken.None));
        fixture.Marketplace.LastSince.Should().Be(checkpointAt.AddSeconds(-1));
        await WithDb(async db => (await db.Orders.CountAsync(x => x.ExternalMarketplaceId == external)).Should().Be(1));
    }

    private async Task Run<T>(Func<T, Task> action) where T : notnull
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<T>());
    }

    private async Task WithDb(Func<OrderPaymentDbContext, Task> action)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<OrderPaymentDbContext>());
    }
}
