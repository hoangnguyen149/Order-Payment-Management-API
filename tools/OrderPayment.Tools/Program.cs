using Microsoft.Data.SqlClient;
using OrderPayment.Tools;

try
{
    var options = ToolOptions.Parse(args);
    if (options is null)
    {
        Console.WriteLine("""
            OrderPayment SQL development tools (.NET 8 / SQL Server 2022)
            Connection: environment variable ConnectionStrings__DefaultConnection (never logged).

            seed --orders 100000 --allow-demo-data
              Applies EF migrations, then adds deterministic historical demo data.
              Database name must end in Demo, Benchmark or Tests. Existing data is preserved.

            benchmark --output artifacts/sql-tuning --allow-benchmark
              Requires >=100000 Orders and a database name ending in Benchmark.
              Rebuilds only the tool-owned dbo.SqlTuningOrders scratch table.
              Writes actual plans, STATISTICS IO/TIME and summary.json into a new run directory.
            """);
        return 0;
    }

    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
    var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new ArgumentException("Set ConnectionStrings__DefaultConnection securely before running SQL tools.");
    SqlConnectionStringBuilder connection;
    try { connection = new SqlConnectionStringBuilder(connectionString); }
    catch (ArgumentException) { throw new ArgumentException("ConnectionStrings__DefaultConnection is not a valid SQL Server connection string."); }
    options.ValidateDatabase(connection.InitialCatalog);

    if (options.Command == "seed")
        await DemoSeeder.RunAsync(connection.ConnectionString, options.Orders, cancellation.Token);
    else
        await SqlBenchmark.RunAsync(connection.ConnectionString, options.OutputDirectory, cancellation.Token);
    return 0;
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Operation cancelled; completed seed batches remain committed and can be resumed.");
    return 130;
}
catch (SqlException exception)
{
    Console.Error.WriteLine($"SQL operation failed (SQL error {exception.Number}). No connection string or SQL parameter values were logged.");
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Operation failed ({exception.GetType().Name}). No credentials were logged.");
    return 1;
}
