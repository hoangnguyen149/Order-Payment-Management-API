using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderPayment.Application.Payments;
using OrderPayment.Domain;
using OrderPayment.Infrastructure.Persistence;

namespace OrderPayment.Infrastructure.Jobs;

public sealed class PaymentReconciler(IServiceScopeFactory scopes, IOptions<JobOptions> options, ILogger<PaymentReconciler> logger)
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderPaymentDbContext>();
        await using var lease = await SqlJobLease.TryAcquireAsync(db.Database.GetConnectionString()!, "reconciliation", ct);
        if (lease is null) return;
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-options.Value.PendingPaymentMinutes);
        var pending = await db.Payments.AsNoTracking().Where(x => x.Status == PaymentStatus.Pending
                && !x.RequiresReview && x.CreatedAt <= cutoff
                && db.Orders.Any(o => o.Id == x.OrderId && o.Status == OrderStatus.Pending))
            .OrderBy(x => x.LastCheckedAt ?? x.CreatedAt).ThenBy(x => x.Id).Take(25).ToListAsync(ct);
        foreach (var payment in pending)
        {
            try
            {
                if (!await MarkCheckedAsync(payment.Id, payment.OrderId, ct)) continue;
                // A fresh scope keeps each payment transaction independent from the polling query.
                await using var paymentScope = scopes.CreateAsyncScope();
                var gateway = paymentScope.ServiceProvider.GetRequiredService<IPaymentGatewayClient>();
                var service = paymentScope.ServiceProvider.GetRequiredService<IPaymentService>();
                GatewayPaymentResponse state;
                try { state = await gateway.GetAsync(payment.PaymentReference, ct); }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // Creation may have timed out before reaching the gateway: reuse the original reference/key.
                    await service.CreateAsync(payment.OrderId, payment.IdempotencyKey, ct);
                    continue;
                }
                if (state.PaymentReference != payment.PaymentReference || state.Amount != payment.Amount)
                    throw new InvalidOperationException("Gateway payment identity/amount mismatch.");
                if (state.Status == PaymentStatus.Pending) continue;
                if (state.Status is not (PaymentStatus.Succeeded or PaymentStatus.Failed))
                    throw new InvalidOperationException("Gateway payment status is invalid.");
                var callbackStatus = state.Status == PaymentStatus.Succeeded
                    ? PaymentCallbackStatus.Succeeded : PaymentCallbackStatus.Failed;
                await service.ApplyWebhookAsync(new PaymentCallback(
                    $"reconcile:{payment.PaymentReference}:{callbackStatus}", payment.PaymentReference, callbackStatus, state.Amount), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                logger.LogWarning("Payment reconciliation {PaymentId} failed with {ErrorType}", payment.Id, error.GetType().Name);
            }
        }
    }

    private async Task<bool> MarkCheckedAsync(Guid paymentId, Guid orderId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderPaymentDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await DatabaseLocks.AcquireOrderAsync(db, orderId, ct);
        var payment = await db.Payments.SingleAsync(x => x.Id == paymentId, ct);
        if (payment.Status != PaymentStatus.Pending || payment.RequiresReview
            || !await db.Orders.AnyAsync(x => x.Id == orderId && x.Status == OrderStatus.Pending, ct)) return false;
        payment.MarkChecked();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
