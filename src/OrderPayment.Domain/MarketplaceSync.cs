namespace OrderPayment.Domain;

public sealed class SyncCheckpoint
{
    private SyncCheckpoint() { }
    public SyncCheckpoint(string name, DateTimeOffset lastSyncedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            throw new DomainRuleException("Tên checkpoint không hợp lệ.");
        Name = name;
        LastSyncedAtUtc = lastSyncedAtUtc.ToUniversalTime();
    }
    public string Name { get; private set; } = string.Empty;
    public DateTimeOffset LastSyncedAtUtc { get; private set; }
    public void AdvanceTo(DateTimeOffset timestamp)
    {
        if (timestamp < LastSyncedAtUtc) throw new DomainRuleException("Checkpoint không được lùi lại.");
        LastSyncedAtUtc = timestamp.ToUniversalTime();
    }
}

public sealed class SyncFailure
{
    private SyncFailure() { }
    public SyncFailure(string externalId, string payload, string error)
    {
        if (string.IsNullOrWhiteSpace(externalId) || externalId.Length > 100 || string.IsNullOrWhiteSpace(payload))
            throw new DomainRuleException("Bản ghi đồng bộ lỗi không hợp lệ.");
        Id = Guid.NewGuid();
        ExternalId = externalId;
        Payload = payload;
        RetryFailed(error, DateTimeOffset.UtcNow.AddMinutes(1));
    }
    public Guid Id { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public string Error { get; private set; } = string.Empty;
    public int Attempts { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }

    public void RetryFailed(string error, DateTimeOffset nextAttemptAt)
    {
        if (ResolvedAt.HasValue) throw new DomainRuleException("Bản ghi đã giải quyết không được lên lịch lại.");
        Attempts++;
        Error = string.IsNullOrWhiteSpace(error) ? "SyncFailed" : error[..Math.Min(error.Length, 1000)];
        NextAttemptAt = nextAttemptAt.ToUniversalTime();
    }
    public void ReplacePayload(string payload, string error)
    {
        if (string.IsNullOrWhiteSpace(payload)) throw new DomainRuleException("Payload đồng bộ không hợp lệ.");
        Payload = payload;
        Error = string.IsNullOrWhiteSpace(error) ? "SyncFailed" : error[..Math.Min(error.Length, 1000)];
        Attempts = 0;
        ResolvedAt = null;
        NextAttemptAt = DateTimeOffset.UtcNow;
    }

    public void Resolve() => ResolvedAt ??= DateTimeOffset.UtcNow;
}
