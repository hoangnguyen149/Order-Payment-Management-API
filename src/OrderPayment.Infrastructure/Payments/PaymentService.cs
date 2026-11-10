using System.Text.Json;
using FluentValidation;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderPayment.Application;
using OrderPayment.Application.Payments;
using OrderPayment.Domain;
using OrderPayment.Infrastructure.Persistence;
using Polly.Timeout;

namespace OrderPayment.Infrastructure.Payments;

internal sealed class PaymentService(OrderPaymentDbContext db, IPaymentGatewayClient gateway,
    IValidator<PaymentCreateRequest> createValidator, IValidator<PaymentCallback> callbackValidator,
    ILogger<PaymentService> logger) : IPaymentService
{
    public async Task<PaymentResponse> CreateAsync(Guid orderId, string idempotencyKey, CancellationToken ct)
    {
        await createValidator.ValidateAndThrowAsync(new PaymentCreateRequest(orderId, idempotencyKey), ct);
        var (payment, submitToGateway) = await ReservePaymentAsync(orderId, idempotencyKey, ct);
        var gatewayUnavailable = false;
        if (submitToGateway)
        {
            // Intent đã commit trước HTTP: crash/timeout không làm mất PaymentReference để đối soát.
            try { await gateway.CreateAsync(payment.PaymentReference, payment.Amount, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException ||
                ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                gatewayUnavailable = true;
                logger.LogWarning("Gateway unavailable for payment {PaymentReference}; reconciliation will retry", payment.PaymentReference);
            }
        }
        var current = await db.Payments.AsNoTracking().SingleAsync(x => x.Id == payment.Id, ct);
        return ToResponse(current, gatewayUnavailable);
    }

    public async Task<IReadOnlyList<PaymentResponse>> GetAsync(Guid orderId, CancellationToken ct)
    {
        if (!await db.Orders.AnyAsync(x => x.Id == orderId, ct)) throw new NotFoundException("Không tìm thấy đơn hàng.");
        var payments = await db.Payments.AsNoTracking().Where(x => x.OrderId == orderId).OrderBy(x => x.CreatedAt).ToListAsync(ct);
        return payments.Select(x => ToResponse(x)).ToArray();
    }

    public async Task<WebhookResult> ApplyWebhookAsync(PaymentCallback callback, CancellationToken ct)
    {
        await callbackValidator.ValidateAndThrowAsync(callback, ct);
        var terminal = callback.Status == PaymentCallbackStatus.Succeeded ? PaymentStatus.Succeeded : PaymentStatus.Failed;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var duplicate = await db.PaymentEvents.AsNoTracking().SingleOrDefaultAsync(x => x.EventId == callback.EventId, ct);
            if (duplicate is not null) return DuplicateResult(duplicate, callback, terminal);
            var orderId = await db.Payments.AsNoTracking().Where(x => x.PaymentReference == callback.PaymentReference)
                .Select(x => (Guid?)x.OrderId).SingleOrDefaultAsync(ct)
                ?? throw new NotFoundException("Không tìm thấy giao dịch thanh toán.");
            await DatabaseLocks.AcquireOrderAsync(db, orderId, ct);
            var existingEvent = await db.PaymentEvents.AsNoTracking().SingleOrDefaultAsync(x => x.EventId == callback.EventId, ct);
            if (existingEvent is not null) return DuplicateResult(existingEvent, callback, terminal);

            var payment = await db.Payments.SingleAsync(x => x.PaymentReference == callback.PaymentReference, ct);
            var order = await db.Orders.Include(x => x.Items).SingleAsync(x => x.Id == orderId, ct);
            if (payment.Amount != callback.Amount) throw new ConflictException("Số tiền callback không khớp giao dịch.");
            if (order.TotalAmount != payment.Amount) throw new ConflictException("Số tiền đơn hàng đã thay đổi; cần đối soát thủ công.");
            var processed = payment.Status == PaymentStatus.Pending && order.Status == OrderStatus.Pending;
            if (processed)
            {
                payment.Apply(terminal);
                order.ChangeStatus(terminal == PaymentStatus.Succeeded ? OrderStatus.Paid : OrderStatus.PaymentFailed);
                if (terminal == PaymentStatus.Failed)
                {
                    var ids = order.Items.Select(x => x.ProductId).ToArray();
                    var products = await db.Products.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
                    foreach (var item in order.Items) products[item.ProductId].Release(item.Quantity);
                }
                AddOutbox(terminal == PaymentStatus.Succeeded ? "PaymentSucceeded" : "PaymentFailed", payment, order);
            }
            else if (payment.Status == PaymentStatus.Pending && order.Status != OrderStatus.Pending && !payment.RequiresReview)
            {
                // Tiền đến sau hủy đơn cần con người đối chiếu/hoàn tiền; không hồi sinh đơn.
                payment.MarkRequiresReview();
                AddOutbox("PaymentReviewRequired", payment, order);
            }
            payment.MarkChecked();
            db.PaymentEvents.Add(new PaymentEvent(callback.EventId, callback.PaymentReference, terminal, callback.Amount,
                processed ? "Processed" : "Ignored"));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new(false, !processed, processed);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // EventId là unique toàn cục: hai payment khác nhau có thể đua trên hai order lock.
            db.ChangeTracker.Clear();
            var existingEvent = await db.PaymentEvents.AsNoTracking().SingleOrDefaultAsync(x => x.EventId == callback.EventId, ct);
            if (existingEvent is null) throw new ConflictException("Callback trùng định danh giao dịch.");
            return DuplicateResult(existingEvent, callback, terminal);
        }
    }

    private async Task<(Payment Payment, bool SubmitToGateway)> ReservePaymentAsync(Guid orderId, string key, CancellationToken ct)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await DatabaseLocks.AcquireOrderAsync(db, orderId, ct);
            var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == orderId, ct)
                ?? throw new NotFoundException("Không tìm thấy đơn hàng.");
            var payment = await db.Payments.SingleOrDefaultAsync(x => x.OrderId == orderId, ct);
            if (payment is not null)
            {
                if (!string.Equals(payment.IdempotencyKey, key, StringComparison.Ordinal))
                    throw new ConflictException("Đơn đã có giao dịch với Idempotency-Key khác.");
                return (payment, payment.Status == PaymentStatus.Pending && order.Status == OrderStatus.Pending && !payment.RequiresReview);
            }
            if (order.Status != OrderStatus.Pending) throw new ConflictException("Chỉ được thanh toán đơn đang Pending.");
            payment = new Payment(orderId, key, order.TotalAmount);
            db.Payments.Add(payment);
            AddOutbox("PaymentInitiated", payment, order);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return (payment, true);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            var payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == orderId, ct);
            if (payment is null || !string.Equals(payment.IdempotencyKey, key, StringComparison.Ordinal))
                throw new ConflictException("Đơn đã có một giao dịch thanh toán khác.");
            var pendingOrder = await db.Orders.AnyAsync(x => x.Id == orderId && x.Status == OrderStatus.Pending, ct);
            return (payment, payment.Status == PaymentStatus.Pending && pendingOrder && !payment.RequiresReview);
        }
    }

    private void AddOutbox(string type, Payment payment, Order order) => db.OutboxMessages.Add(new OutboxMessage(type,
        JsonSerializer.Serialize(new { payment.Id, payment.OrderId, payment.PaymentReference, payment.Amount,
            PaymentStatus = payment.Status.ToString(), OrderStatus = order.Status.ToString(), payment.RequiresReview })));

    private static WebhookResult DuplicateResult(PaymentEvent existing, PaymentCallback callback, PaymentStatus terminal)
    {
        if (!string.Equals(existing.PaymentReference, callback.PaymentReference, StringComparison.Ordinal)
            || existing.Status != terminal || existing.Amount != callback.Amount)
            throw new ConflictException("EventId đã được sử dụng cho một callback khác.");
        return new(true, existing.Outcome == "Ignored", false);
    }

    private static PaymentResponse ToResponse(Payment payment, bool unavailable = false) =>
        new(payment.Id, payment.OrderId, payment.PaymentReference, payment.Amount, payment.Status,
            payment.CreatedAt, payment.UpdatedAt, unavailable, payment.RequiresReview);

    private static bool IsUniqueViolation(DbUpdateException ex) => ex.InnerException is SqlException { Number: 2601 or 2627 };
}
