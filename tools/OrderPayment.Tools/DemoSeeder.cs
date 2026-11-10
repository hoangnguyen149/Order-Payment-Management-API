using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrderPayment.Infrastructure.Persistence;

namespace OrderPayment.Tools;

internal static class DemoSeeder
{
    public static async Task RunAsync(string connectionString, int orders, CancellationToken ct)
    {
        // EF8 does not serialize concurrent migrations. Hold a DB-specific session lock in master,
        // which exists even when this demo database has not been created yet.
        var masterString = new SqlConnectionStringBuilder(connectionString);
        var database = masterString.InitialCatalog;
        masterString.InitialCatalog = "master";
        await using var migrationLease = new SqlConnection(masterString.ConnectionString);
        await migrationLease.OpenAsync(ct);
        await SqlBenchmark.AcquireLockAsync(migrationLease, "OrderPayment.DemoSeed.Database." + database, ct);
        var options = new DbContextOptionsBuilder<OrderPaymentDbContext>().UseSqlServer(connectionString).Options;
        await using (var db = new OrderPaymentDbContext(options)) await db.Database.MigrateAsync(ct);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await SqlBenchmark.AcquireLockAsync(connection, "OrderPayment.DemoSeed.v1", ct);
        await ExecuteAsync(connection, SqlResources.Read("SeedPrincipals.sql"), null, ct);
        for (var start = 1; start <= orders; start += 5000)
        {
            var end = Math.Min(start + 4999, orders);
            await ExecuteAsync(connection, SqlResources.Read("SeedOrders.sql"), command =>
            {
                command.Parameters.Add("@Start", SqlDbType.Int).Value = start;
                command.Parameters.Add("@End", SqlDbType.Int).Value = end;
            }, ct);
            Console.WriteLine($"Seed batch complete: deterministic order IDs 1..{end}. Existing IDs preserved.");
        }
        using var count = new SqlCommand("SELECT COUNT_BIG(1) FROM dbo.Orders WHERE LOWER(CONVERT(varchar(36), Id)) LIKE 'd2000000-0000-0000-0000-%';", connection);
        var result = Convert.ToInt64(await count.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        Console.WriteLine($"Historical demo orders present: {result}. No active Pending reservations were generated.");
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, Action<SqlCommand>? configure, CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        configure?.Invoke(command);
        await command.ExecuteNonQueryAsync(ct);
    }
}
