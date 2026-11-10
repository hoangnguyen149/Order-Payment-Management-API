namespace OrderPayment.Domain;

public sealed class OutboxMessage
{
    private OutboxMessage() { }

    public OutboxMessage(string type, string payload)
    {
        if (string.IsNullOrWhiteSpace(type) || type.Length > 100 || string.IsNullOrWhiteSpace(payload))
            throw new DomainRuleException("Sự kiện outbox không hợp lệ.");
        Id = Guid.NewGuid();
        Type = type;
        Payload = payload;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public string? LastError { get; private set; }

    public void MarkProcessed()
    {
        if (ProcessedAt.HasValue) return;
        ProcessedAt = DateTimeOffset.UtcNow;
        NextAttemptAt = null;
        LastError = null;
    }

    public void MarkFailed(string error, DateTimeOffset nextAttemptAt)
    {
        if (ProcessedAt.HasValue) throw new DomainRuleException("Outbox đã xử lý không được lên lịch lại.");
        Attempts++;
        LastError = string.IsNullOrWhiteSpace(error) ? "DeliveryFailed" : error[..Math.Min(error.Length, 1000)];
        NextAttemptAt = nextAttemptAt.ToUniversalTime();
    }
}

// Receipt mô phỏng consumer chống nhận lại cùng message; MessageId là khóa duy nhất ở DB.
public sealed class OutboxDelivery
{
    private OutboxDelivery() { }
    public OutboxDelivery(Guid messageId)
    {
        if (messageId == Guid.Empty) throw new DomainRuleException("MessageId không hợp lệ.");
        MessageId = messageId;
        DeliveredAt = DateTimeOffset.UtcNow;
    }
    public Guid MessageId { get; private set; }
    public DateTimeOffset DeliveredAt { get; private set; }
}
