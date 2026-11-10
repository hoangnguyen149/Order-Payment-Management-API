using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderPayment.Application.Marketplace;
using OrderPayment.Domain;
using OrderPayment.Infrastructure.Persistence;

namespace OrderPayment.Infrastructure.Jobs;

public sealed class MarketplaceSyncProcessor(IServiceScopeFactory scopes, IMarketplaceClient client, ILogger<MarketplaceSyncProcessor> logger)
{
    private const string CheckpointName = "mock-marketplace";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderPaymentDbContext>();
        await using var lease = await SqlJobLease.TryAcquireAsync(db.Database.GetConnectionString()!, "marketplace-sync", ct);
        if (lease is null) return;
        await RetryFailuresAsync(ct);
        var checkpoint = await db.SyncCheckpoints.SingleOrDefaultAsync(x => x.Name == CheckpointName, ct);
        if (checkpoint is null)
        {
            checkpoint = new SyncCheckpoint(CheckpointName, DateTimeOffset.UnixEpoch);
            db.SyncCheckpoints.Add(checkpoint);
            await db.SaveChangesAsync(ct);
        }
        var since = checkpoint.LastSyncedAtUtc > DateTimeOffset.UnixEpoch
            ? checkpoint.LastSyncedAtUtc.AddSeconds(-1) : DateTimeOffset.UnixEpoch;
        var watermark = DateTimeOffset.UtcNow;
        // Clock movement backwards must not move the durable checkpoint backwards.
        if (watermark <= checkpoint.LastSyncedAtUtc) return;
        string? cursor = null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        for (var pageNumber = 0; ; pageNumber++)
        {
            if (pageNumber >= 1000) throw new InvalidOperationException("Marketplace paging limit exceeded; checkpoint preserved.");
            var page = await client.GetOrdersAsync(since, watermark, cursor, ct);
            if (page.Items is null || page.Items.Count > 100) throw new InvalidOperationException("Invalid marketplace page.");
            foreach (var order in page.Items)
            {
                if (order.UpdatedAt < since || order.UpdatedAt > watermark)
                    throw new InvalidOperationException("Marketplace returned a row outside the requested snapshot.");
                try
                {
                    var coveredThrough = await ImportAsync(order, ct);
                    await ResolveFailureAsync(FailureKey(order), coveredThrough, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    // The failed record is durable BEFORE advancing the checkpoint past it.
                    await RecordFailureAsync(order, error, ct);
                    logger.LogWarning("Marketplace import failure recorded: {ErrorType}", error.GetType().Name);
                }
            }
            cursor = page.NextCursor;
            if (cursor is null) break;
            if (cursor.Length > 2048 || !cursors.Add(cursor))
                throw new InvalidOperationException("Marketplace returned an invalid/repeated cursor.");
        }
        checkpoint.AdvanceTo(watermark);
        await db.SaveChangesAsync(ct);
    }

    private async Task RetryFailuresAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderPaymentDbContext>();
        var now = DateTimeOffset.UtcNow;
        var failures = await db.SyncFailures.AsNoTracking().Where(x => x.ResolvedAt == null && x.NextAttemptAt <= now)
            .OrderBy(x => x.NextAttemptAt).Take(20).ToListAsync(ct);
        foreach (var failure in failures)
        {
            try
            {
                var order = JsonSerializer.Deserialize<MarketplaceOrder>(failure.Payload, Json)
                    ?? throw new ImportException("InvalidStoredPayload");
                var coveredThrough = await ImportAsync(order, ct);
                await ResolveFailureAsync(failure.ExternalId, coveredThrough, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                await using var retryScope = scopes.CreateAsyncScope();
                var retryDb = retryScope.ServiceProvider.GetRequiredService<OrderPaymentDbContext>();
                var record = await retryDb.SyncFailures.SingleAsync(x => x.Id == failure.Id, ct);
                record.RetryFailed(ErrorCode(error), OutboxProcessor.RetryAt(record.Attempts));
                await retryDb.SaveChangesAsync(ct);
            }
        }
    }

    private async Task<DateTimeOffset> ImportAsync(MarketplaceOrder input, CancellationToken ct)
    {
        Validate(input);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderPaymentDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var existingId = await db.Orders.AsNoTracking().Where(x => x.ExternalMarketplaceId == input.ExternalId)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        if (existingId is { } orderId) await DatabaseLocks.AcquireOrderAsync(db, orderId, ct);
        var order = await db.Orders.Include(x => x.Items).SingleOrDefaultAsync(x => x.ExternalMarketplaceId == input.ExternalId, ct);
        if (order?.MarketplaceUpdatedAt is { } currentTimestamp && currentTimestamp >= input.UpdatedAt) return currentTimestamp;

        var email = input.CustomerEmail.Trim().ToLowerInvariant();
        var customer = await db.Customers.SingleOrDefaultAsync(x => x.Email == email, ct);
        if (customer is null)
        {
            customer = new Customer(input.CustomerName, email, null);
            db.Customers.Add(customer);
        }
        var requested = input.Items.ToDictionary(x => x.Sku.Trim().ToUpperInvariant(), x => x.Quantity, StringComparer.Ordinal);
        var skus = requested.Keys.ToArray();
        var oldIds = order?.Items.Select(x => x.ProductId).ToArray() ?? [];
        var products = await db.Products.Where(x => skus.Contains(x.Sku) || oldIds.Contains(x.Id)).ToListAsync(ct);
        var catalog = products.ToDictionary(x => x.Sku, StringComparer.Ordinal);
        if (skus.Any(x => !catalog.ContainsKey(x))) throw new ImportException("ProductSkuNotFound");

        var sameTerms = order is not null && order.CustomerId == customer.Id && order.Items.Count == requested.Count
            && order.Items.All(x => products.Any(p => p.Id == x.ProductId && requested.TryGetValue(p.Sku, out var quantity) && quantity == x.Quantity));
        if (sameTerms)
        {
            // A source heartbeat does not reprice an existing order or reserve stock twice.
            order!.MarkMarketplaceUpdated(input.UpdatedAt);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return input.UpdatedAt;
        }
        if (order is not null)
        {
            if (order.Status != OrderStatus.Pending) throw new ImportException("OrderNotEditable");
            if (await db.Payments.AnyAsync(x => x.OrderId == order.Id, ct)) throw new ImportException("OrderHasPayment");
            var byId = products.ToDictionary(x => x.Id);
            foreach (var old in order.Items) byId[old.ProductId].Release(old.Quantity);
        }
        var lines = new List<OrderItem>();
        foreach (var item in requested)
        {
            var product = catalog[item.Key];
            if (product.StockQuantity < item.Value) throw new ImportException("InsufficientStock");
            product.Reserve(item.Value);
            lines.Add(new OrderItem(product.Id, product.Name, product.Price, item.Value));
        }
        if (order is null)
        {
            order = new Order(customer.Id, lines);
            order.AssignMarketplaceId(input.ExternalId, input.UpdatedAt);
            db.Orders.Add(order);
        }
        else
        {
            var oldLines = order.Items.ToArray();
            order.ReplaceItems(customer.Id, lines);
            order.MarkMarketplaceUpdated(input.UpdatedAt);
            db.OrderItems.RemoveRange(oldLines);
            db.OrderItems.AddRange(order.Items);
            db.Entry(order).Property(x => x.TotalAmount).IsModified = true;
        }
        db.OutboxMessages.Add(new OutboxMessage("MarketplaceOrderUpserted", JsonSerializer.Serialize(new { order.Id, input.ExternalId })));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return input.UpdatedAt;
    }

    private async Task RecordFailureAsync(MarketplaceOrder order, Exception error, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(order, Json);
        var key = FailureKey(order);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderPaymentDbContext>();
        var failure = await db.SyncFailures.SingleOrDefaultAsync(x => x.ExternalId == key, ct);
        if (failure is null) db.SyncFailures.Add(new SyncFailure(key, payload, ErrorCode(error)));
        else
        {
            DateTimeOffset previousTimestamp;
            try { previousTimestamp = JsonSerializer.Deserialize<MarketplaceOrder>(failure.Payload, Json)?.UpdatedAt ?? DateTimeOffset.MinValue; }
            catch (JsonException) { previousTimestamp = DateTimeOffset.MinValue; }
            if (order.UpdatedAt > previousTimestamp) failure.ReplacePayload(payload, ErrorCode(error));
            if (failure.ResolvedAt is null) failure.RetryFailed(ErrorCode(error), OutboxProcessor.RetryAt(failure.Attempts));
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task ResolveFailureAsync(string key, DateTimeOffset coveredThrough, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderPaymentDbContext>();
        var failure = await db.SyncFailures.SingleOrDefaultAsync(x => x.ExternalId == key && x.ResolvedAt == null, ct);
        if (failure is null) return;
        try
        {
            var failedVersion = JsonSerializer.Deserialize<MarketplaceOrder>(failure.Payload, Json);
            if (failedVersion is null || failedVersion.UpdatedAt > coveredThrough) return;
        }
        catch (JsonException) { return; }
        failure.Resolve();
        await db.SaveChangesAsync(ct);
    }

    private static void Validate(MarketplaceOrder order)
    {
        if (string.IsNullOrWhiteSpace(order.ExternalId) || order.ExternalId.Length > 100 || order.ExternalId != order.ExternalId.Trim()
            || string.IsNullOrWhiteSpace(order.CustomerName) || order.CustomerName.Length > 200
            || string.IsNullOrWhiteSpace(order.CustomerEmail) || order.CustomerEmail.Length > 254
            || !MailAddress.TryCreate(order.CustomerEmail, out _) || order.UpdatedAt < DateTimeOffset.UnixEpoch
            || order.Items is null || order.Items.Count is < 1 or > 100
            || order.Items.Any(x => x is null || string.IsNullOrWhiteSpace(x.Sku) || x.Sku.Length > 64 || x.Quantity is < 1 or > 10000)
            || order.Items.Select(x => x.Sku.Trim().ToUpperInvariant()).Distinct().Count() != order.Items.Count)
            throw new ImportException("InvalidMarketplaceOrder");
    }

    private static string FailureKey(MarketplaceOrder order) => !string.IsNullOrWhiteSpace(order.ExternalId) && order.ExternalId.Length <= 100 && order.ExternalId == order.ExternalId.Trim()
        ? order.ExternalId : "invalid:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(order, Json))));
    private static string ErrorCode(Exception error) => error is ImportException ? error.Message : error.GetType().Name;
    private sealed class ImportException(string code) : Exception(code);
}
