using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderPayment.Domain;
using OrderPayment.Infrastructure.Persistence;

namespace OrderPayment.Infrastructure.Jobs;

public interface IOutboxSink
{
    Task DeliverAsync(Guid messageId, string eventType, CancellationToken ct);
}

internal sealed class LoggingOutboxSink(ILogger<LoggingOutboxSink> logger) : IOutboxSink
{
    public Task DeliverAsync(Guid messageId, string eventType, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Mô phỏng email: không ghi payload, địa chỉ email hay dữ liệu khách hàng vào log.
        logger.LogInformation("Simulated delivery {MessageId} of {EventType}", messageId, eventType);
        return Task.CompletedTask;
    }
}

public sealed class OutboxProcessor(IServiceScopeFactory scopes, IOutboxSink sink, ILogger<OutboxProcessor> logger)
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        await using var queryScope = scopes.CreateAsyncScope();
        var queryDb = queryScope.ServiceProvider.GetRequiredService<OrderPaymentDbContext>();
        await using var lease = await SqlJobLease.TryAcquireAsync(queryDb.Database.GetConnectionString()!, "outbox", ct);
        if (lease is null) return;
        var now = DateTimeOffset.UtcNow;
        var ids = await queryDb.OutboxMessages.AsNoTracking()
            .Where(x => x.ProcessedAt == null && (x.NextAttemptAt == null || x.NextAttemptAt <= now))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Select(x => x.Id).Take(25).ToListAsync(ct);
        foreach (var id in ids)
        {
            try { await DeliverOneAsync(id, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                logger.LogWarning("Outbox {MessageId} delivery failed with {ErrorType}", id, error.GetType().Name);
                await using var retryScope = scopes.CreateAsyncScope();
                var retryDb = retryScope.ServiceProvider.GetRequiredService<OrderPaymentDbContext>();
                var message = await retryDb.OutboxMessages.SingleAsync(x => x.Id == id, ct);
                if (message.ProcessedAt is not null) continue;
                message.MarkFailed(error.GetType().Name, RetryAt(message.Attempts));
                await retryDb.SaveChangesAsync(ct);
            }
        }
    }

    private async Task DeliverOneAsync(Guid id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderPaymentDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var message = await db.OutboxMessages.SingleAsync(x => x.Id == id, ct);
        if (message.ProcessedAt is not null) return;
        if (!await db.OutboxDeliveries.AnyAsync(x => x.MessageId == id, ct))
        {
            await sink.DeliverAsync(message.Id, message.Type, ct);
            db.OutboxDeliveries.Add(new OutboxDelivery(message.Id));
        }
        message.MarkProcessed();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    internal static DateTimeOffset RetryAt(int attempts) => DateTimeOffset.UtcNow
        .AddSeconds(Math.Min(3600, 5 * Math.Pow(2, Math.Min(attempts, 10))));
}
