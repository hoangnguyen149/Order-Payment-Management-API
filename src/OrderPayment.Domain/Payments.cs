namespace OrderPayment.Domain;

public enum PaymentStatus { Pending, Succeeded, Failed }

public sealed class Payment
{
    private Payment() { }

    public Payment(Guid orderId, string idempotencyKey, decimal amount)
    {
        if (orderId == Guid.Empty) throw new DomainRuleException("Đơn hàng không hợp lệ.");
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
            throw new DomainRuleException("Idempotency-Key phải có từ 1 đến 100 ký tự.");
        if (amount <= 0 || amount > 1_000_000_000_000_000m || decimal.Round(amount, 2) != amount)
            throw new DomainRuleException("Số tiền thanh toán không hợp lệ.");
        Id = Guid.NewGuid();
        OrderId = orderId;
        PaymentReference = Guid.NewGuid().ToString("N");
        IdempotencyKey = idempotencyKey;
        Amount = amount;
        Status = PaymentStatus.Pending;
        CreatedAt = UpdatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public string PaymentReference { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public PaymentStatus Status { get; private set; }
    public bool RequiresReview { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? LastCheckedAt { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    // Trạng thái cuối là bất biến: callback đến muộn không được đảo ngược kết quả trước đó.
    public bool Apply(PaymentStatus terminal)
    {
        if (terminal is not (PaymentStatus.Succeeded or PaymentStatus.Failed))
            throw new DomainRuleException("Kết quả thanh toán phải là Succeeded hoặc Failed.");
        if (Status != PaymentStatus.Pending) return false;
        Status = terminal;
        UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    public bool MarkRequiresReview()
    {
        if (RequiresReview) return false;
        RequiresReview = true;
        UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    public void MarkChecked() => LastCheckedAt = DateTimeOffset.UtcNow;
}

public sealed class PaymentEvent
{
    private PaymentEvent() { }

    public PaymentEvent(string eventId, string paymentReference, PaymentStatus status, decimal amount, string outcome)
    {
        if (string.IsNullOrWhiteSpace(eventId) || eventId.Length > 128)
            throw new DomainRuleException("EventId không hợp lệ.");
        if (string.IsNullOrWhiteSpace(paymentReference) || paymentReference.Length > 64)
            throw new DomainRuleException("PaymentReference không hợp lệ.");
        if (status is not (PaymentStatus.Succeeded or PaymentStatus.Failed))
            throw new DomainRuleException("Trạng thái callback không hợp lệ.");
        if (amount <= 0 || amount > 1_000_000_000_000_000m || decimal.Round(amount, 2) != amount)
            throw new DomainRuleException("Số tiền callback không hợp lệ.");
        if (outcome is not ("Processed" or "Ignored"))
            throw new DomainRuleException("Kết quả xử lý callback không hợp lệ.");
        Id = Guid.NewGuid();
        EventId = eventId;
        PaymentReference = paymentReference;
        Status = status;
        Amount = amount;
        Outcome = outcome;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public string EventId { get; private set; } = string.Empty;
    public string PaymentReference { get; private set; } = string.Empty;
    public PaymentStatus Status { get; private set; }
    public decimal Amount { get; private set; }
    public string Outcome { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
}
