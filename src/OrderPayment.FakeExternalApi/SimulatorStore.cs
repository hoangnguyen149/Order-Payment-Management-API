using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace OrderPayment.FakeExternalApi;

// SQLite is local durable state for a single simulator instance; not a production gateway store.
public sealed class SimulatorStore(IOptions<FakeOptions> options)
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(options.Value.DatabasePath),
        Mode = SqliteOpenMode.ReadWriteCreate,
        DefaultTimeout = 10
    }.ToString();

    public async Task InitializeAsync(CancellationToken ct)
    {
        var path = Path.GetFullPath(options.Value.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Payments (
                PaymentReference TEXT NOT NULL PRIMARY KEY,
                AmountCents INTEGER NOT NULL CHECK(AmountCents>0),
                Status TEXT NOT NULL CHECK(Status IN ('Pending','Succeeded','Failed')),
                EventId TEXT NULL);
            CREATE TABLE IF NOT EXISTS PaymentAttempts (
                PaymentReference TEXT NOT NULL PRIMARY KEY, Attempts INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS MarketplaceOrders (
                ExternalId TEXT NOT NULL PRIMARY KEY, Revision INTEGER NOT NULL,
                UpdatedAtTicks INTEGER NOT NULL, Payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS MarketplaceChanges (
                Revision INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ExternalId TEXT NOT NULL, UpdatedAtTicks INTEGER NOT NULL, Payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_MarketplaceChanges_Time_Revision ON MarketplaceChanges(UpdatedAtTicks,Revision);
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<PaymentRecord?> GetPaymentAsync(string reference, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return await ReadPaymentAsync(connection, reference, null, ct);
    }

    public async Task<PaymentRecord> CreatePaymentAsync(string reference, decimal amount, CancellationToken ct)
    {
        await _writer.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            using var transaction = connection.BeginTransaction();
            var existing = await ReadPaymentAsync(connection, reference, transaction, ct);
            if (existing is not null)
            {
                if (existing.Amount != amount) throw new SimulatorConflictException("Payment reference is already bound to another amount.");
                return existing;
            }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO Payments(PaymentReference,AmountCents,Status) VALUES ($reference,$amount,'Pending');";
            command.Parameters.AddWithValue("$reference", reference);
            command.Parameters.AddWithValue("$amount", ToCents(amount));
            await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return new(reference, "Pending", amount, null);
        }
        finally { _writer.Release(); }
    }

    public async Task<int> RecordAttemptAsync(string reference, CancellationToken ct)
    {
        await _writer.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO PaymentAttempts(PaymentReference,Attempts) VALUES ($reference,1)
                ON CONFLICT(PaymentReference) DO UPDATE SET Attempts=Attempts+1 RETURNING Attempts;
                """;
            command.Parameters.AddWithValue("$reference", reference);
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        finally { _writer.Release(); }
    }

    public async Task<PaymentRecord?> SettlePaymentAsync(string reference, string status, CancellationToken ct)
    {
        await _writer.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            using var transaction = connection.BeginTransaction();
            var payment = await ReadPaymentAsync(connection, reference, transaction, ct);
            if (payment is null) return null;
            if (payment.Status != "Pending")
            {
                if (payment.Status != status) throw new SimulatorConflictException("A terminal payment cannot be settled to another status.");
                return payment;
            }
            var eventId = Guid.NewGuid().ToString("N");
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE Payments SET Status=$status,EventId=$event WHERE PaymentReference=$reference;";
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$event", eventId);
            command.Parameters.AddWithValue("$reference", reference);
            await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return payment with { Status = status, EventId = eventId };
        }
        finally { _writer.Release(); }
    }

    public async Task<MarketplaceOrder> UpsertMarketplaceAsync(MarketplaceWriteRequest request, CancellationToken ct)
    {
        await _writer.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            using var transaction = connection.BeginTransaction();
            MarketplaceOrder? previous = null;
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT Payload FROM MarketplaceOrders WHERE ExternalId=$id;";
                read.Parameters.AddWithValue("$id", request.ExternalId);
                if (await read.ExecuteScalarAsync(ct) is string payload)
                    previous = JsonSerializer.Deserialize<MarketplaceOrder>(payload, JsonOptions);
            }
            var normalized = new MarketplaceOrder(request.ExternalId, previous?.UpdatedAt ?? DateTimeOffset.UnixEpoch,
                request.CustomerEmail.Trim().ToLowerInvariant(), request.CustomerName.Trim(),
                request.Items.Select(x => new MarketplaceLine(x.Sku.Trim().ToUpperInvariant(), x.Quantity)).ToArray());
            if (previous is not null && JsonSerializer.Serialize(previous, JsonOptions) == JsonSerializer.Serialize(normalized, JsonOptions))
                return previous;

            long lastTicks;
            await using (var clock = connection.CreateCommand())
            {
                clock.Transaction = transaction;
                clock.CommandText = "SELECT COALESCE(MAX(UpdatedAtTicks),0) FROM MarketplaceChanges;";
                lastTicks = Convert.ToInt64(await clock.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }
            // Client timestamps are never authoritative: backdating a new write could lose it behind a checkpoint.
            var timestamp = new DateTimeOffset(Math.Max(DateTimeOffset.UtcNow.UtcTicks, lastTicks + 1), TimeSpan.Zero);
            var order = normalized with { UpdatedAt = timestamp };
            var json = JsonSerializer.Serialize(order, JsonOptions);
            long revision;
            await using (var change = connection.CreateCommand())
            {
                change.Transaction = transaction;
                change.CommandText = "INSERT INTO MarketplaceChanges(ExternalId,UpdatedAtTicks,Payload) VALUES ($id,$updated,$payload) RETURNING Revision;";
                change.Parameters.AddWithValue("$id", order.ExternalId);
                change.Parameters.AddWithValue("$updated", timestamp.UtcTicks);
                change.Parameters.AddWithValue("$payload", json);
                revision = Convert.ToInt64(await change.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }
            await using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO MarketplaceOrders(ExternalId,Revision,UpdatedAtTicks,Payload) VALUES ($id,$revision,$updated,$payload)
                    ON CONFLICT(ExternalId) DO UPDATE SET Revision=excluded.Revision,UpdatedAtTicks=excluded.UpdatedAtTicks,Payload=excluded.Payload;
                    """;
                upsert.Parameters.AddWithValue("$id", order.ExternalId);
                upsert.Parameters.AddWithValue("$revision", revision);
                upsert.Parameters.AddWithValue("$updated", timestamp.UtcTicks);
                upsert.Parameters.AddWithValue("$payload", json);
                await upsert.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return order;
        }
        finally { _writer.Release(); }
    }

    public async Task<(IReadOnlyList<MarketplaceOrder> Orders, long SnapshotRevision, long? NextRevision)> ReadMarketplaceAsync(
        DateTimeOffset since, DateTimeOffset until, FeedCursor? cursor, int pageSize, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var snapshot = cursor?.SnapshotRevision;
        if (snapshot is null)
        {
            await using var latest = connection.CreateCommand();
            latest.CommandText = "SELECT COALESCE(MAX(Revision),0) FROM MarketplaceChanges;";
            snapshot = Convert.ToInt64(await latest.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Revision,Payload FROM MarketplaceChanges
            WHERE Revision>$last AND Revision<=$snapshot AND UpdatedAtTicks>=$since AND UpdatedAtTicks<=$until
            ORDER BY Revision LIMIT $take;
            """;
        command.Parameters.AddWithValue("$last", cursor?.LastRevision ?? 0);
        command.Parameters.AddWithValue("$snapshot", snapshot);
        command.Parameters.AddWithValue("$since", since.UtcTicks);
        command.Parameters.AddWithValue("$until", until.UtcTicks);
        command.Parameters.AddWithValue("$take", pageSize + 1);
        var found = new List<(long Revision, MarketplaceOrder Order)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            found.Add((reader.GetInt64(0), JsonSerializer.Deserialize<MarketplaceOrder>(reader.GetString(1), JsonOptions)!));
        var hasNext = found.Count > pageSize;
        var page = found.Take(pageSize).ToArray();
        return (page.Select(x => x.Order).ToArray(), snapshot.Value, hasNext ? page[^1].Revision : null);
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1;";
        await command.ExecuteScalarAsync(ct);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static async Task<PaymentRecord?> ReadPaymentAsync(SqliteConnection connection, string reference, SqliteTransaction? transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT PaymentReference,Status,AmountCents,EventId FROM Payments WHERE PaymentReference=$reference;";
        command.Parameters.AddWithValue("$reference", reference);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2) / 100m, reader.IsDBNull(3) ? null : reader.GetString(3))
            : null;
    }

    private static long ToCents(decimal amount) => checked((long)(amount * 100m));
}
