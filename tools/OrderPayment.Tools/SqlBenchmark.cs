using System.Data;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace OrderPayment.Tools;

internal static class SqlBenchmark
{
    private const string BeforeQuery = """
        SELECT Id,CustomerId,CreatedAt,Status,TotalAmount
        FROM dbo.SqlTuningOrders
        WHERE CustomerId=@CustomerId AND CONVERT(date,CreatedAt)=@Day
        ORDER BY CreatedAt,Id
        OPTION (RECOMPILE);
        """;
    private const string AfterQuery = """
        SELECT Id,CustomerId,CreatedAt,Status,TotalAmount
        FROM dbo.SqlTuningOrders
        WHERE CustomerId=@CustomerId AND CreatedAt>=@FromUtc AND CreatedAt<@ToUtcExclusive
        ORDER BY CreatedAt,Id
        OPTION (RECOMPILE);
        """;

    public static async Task RunAsync(string connectionString, string outputDirectory, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await AcquireLockAsync(connection, "OrderPayment.SqlBenchmark.v1", ct);
        await ExecuteAsync(connection, "SET LANGUAGE us_english;", ct);
        await ExecuteAsync(connection, SqlResources.Read("PrepareBenchmark.sql"), ct);
        var sample = await ReadSampleAsync(connection, ct);
        var runDirectory = Path.Combine(Path.GetFullPath(outputDirectory), $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(runDirectory);
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "before.sql"), BeforeQuery, ct);
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "after.sql"), AfterQuery, ct);

        // Warm the data cache once per variant; do not clear global SQL Server caches.
        await MeasureAsync(connection, BeforeQuery, sample.CustomerId, sample.Day, ct);
        var before = await RunVariantAsync(connection, "before", BeforeQuery, sample.CustomerId, sample.Day, runDirectory, ct);
        await ExecuteAsync(connection, """
            CREATE INDEX IX_SqlTuningOrders_CustomerId_CreatedAt
            ON dbo.SqlTuningOrders(CustomerId,CreatedAt) INCLUDE(Status,TotalAmount);
            UPDATE STATISTICS dbo.SqlTuningOrders WITH FULLSCAN;
            """, ct);
        await MeasureAsync(connection, AfterQuery, sample.CustomerId, sample.Day, ct);
        var after = await RunVariantAsync(connection, "after", AfterQuery, sample.CustomerId, sample.Day, runDirectory, ct);
        var checksums = before.Concat(after).Select(x => (x.Rows, x.ResultSha256)).Distinct().ToList();
        if (checksums.Count != 1) throw new InvalidOperationException("Benchmark variants produced different result sets; results are invalid.");
        var statisticsComplete = before.Concat(after).All(x => x.LogicalReads.HasValue && x.SqlCpuMs.HasValue && x.SqlElapsedMs.HasValue);

        var server = await ReadServerAsync(connection, ct);
        var summary = new
        {
            measuredAtUtc = DateTimeOffset.UtcNow,
            server.ProductVersion, server.Edition, server.CompatibilityLevel,
            rowsInScratchTable = sample.TableCount,
            sample.CustomerId,
            dayUtc = sample.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            logicalProcessorCount = Environment.ProcessorCount,
            repetitions = 3,
            cache = "one warm-up per variant; no cache flush",
            experiment = "before: clustered PK only + CONVERT(date); after: covering customer/date index + half-open UTC range",
            equivalentResults = true,
            statisticsComplete,
            before, after,
            median = new
            {
                beforeLogicalReads = Median(before.Select(x => x.LogicalReads)),
                afterLogicalReads = Median(after.Select(x => x.LogicalReads)),
                beforeSqlCpuMs = Median(before.Select(x => x.SqlCpuMs)),
                afterSqlCpuMs = Median(after.Select(x => x.SqlCpuMs)),
                beforeSqlElapsedMs = Median(before.Select(x => x.SqlElapsedMs)),
                afterSqlElapsedMs = Median(after.Select(x => x.SqlElapsedMs))
            },
            limitations = "Isolated synthetic snapshot; changes query and index together. Millisecond metrics may round to zero; workload and write costs not benchmarked."
        };
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }), ct);
        if (!statisticsComplete)
        {
            Console.Error.WriteLine($"Statistics capture was incomplete. Inspect raw statistics in {runDirectory}; do not use these results for a performance claim.");
            throw new InvalidOperationException("Incomplete benchmark metrics.");
        }
        Console.WriteLine($"Benchmark complete: equivalent results across six measured executions. Artifacts: {runDirectory}");
    }

    public static async Task AcquireLockAsync(SqlConnection connection, string resource, CancellationToken ct)
    {
        await using var command = new SqlCommand("""
            DECLARE @result int;
            EXEC @result=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=10000;
            IF @result<0 THROW 51003,'Another SQL tool instance owns the lock.',1;
            """, connection);
        command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = resource;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<Measurement>> RunVariantAsync(SqlConnection connection, string variant, string query,
        Guid customerId, DateOnly day, string directory, CancellationToken ct)
    {
        var results = new List<Measurement>();
        for (var i = 1; i <= 3; i++)
        {
            var measurement = await MeasureAsync(connection, query, customerId, day, ct);
            await File.WriteAllTextAsync(Path.Combine(directory, $"{variant}-{i}.sqlplan"), measurement.PlanXml, ct);
            await File.WriteAllTextAsync(Path.Combine(directory, $"{variant}-{i}-statistics.txt"), measurement.Statistics, ct);
            results.Add(measurement with { PlanXml = "", Statistics = "" });
        }
        return results;
    }

    private static async Task<Measurement> MeasureAsync(SqlConnection connection, string query, Guid customerId, DateOnly day, CancellationToken ct)
    {
        var messages = new StringBuilder();
        void OnMessage(object sender, SqlInfoMessageEventArgs args) => messages.AppendLine(args.Message);
        connection.InfoMessage += OnMessage;
        try
        {
            await using var command = new SqlCommand("SET STATISTICS IO ON; SET STATISTICS TIME ON; SET STATISTICS XML ON;\n" + query +
                "\nSET STATISTICS XML OFF; SET STATISTICS TIME OFF; SET STATISTICS IO OFF;", connection) { CommandTimeout = 120 };
            command.Parameters.Add("@CustomerId", SqlDbType.UniqueIdentifier).Value = customerId;
            command.Parameters.Add("@Day", SqlDbType.Date).Value = day.ToDateTime(TimeOnly.MinValue);
            var fromUtc = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            command.Parameters.Add("@FromUtc", SqlDbType.DateTimeOffset).Value = fromUtc;
            command.Parameters.Add("@ToUtcExclusive", SqlDbType.DateTimeOffset).Value = fromUtc.AddDays(1);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var rows = 0;
            var plans = new List<string>();
            var stopwatch = Stopwatch.StartNew();
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                do
                {
                    while (await reader.ReadAsync(ct))
                    {
                        if (reader.FieldCount == 1 && reader.GetName(0).Contains("Showplan", StringComparison.OrdinalIgnoreCase))
                        {
                            var value = reader.GetValue(0);
                            plans.Add(value is SqlXml xml ? xml.Value : value.ToString() ?? "");
                            continue;
                        }
                        if (reader.FieldCount != 5) continue;
                        var line = string.Join('|', reader.GetGuid(0).ToString("D"), reader.GetGuid(1).ToString("D"),
                            reader.GetDateTimeOffset(2).ToString("O", CultureInfo.InvariantCulture), reader.GetString(3),
                            reader.GetDecimal(4).ToString(CultureInfo.InvariantCulture)) + "\n";
                        hash.AppendData(Encoding.UTF8.GetBytes(line));
                        rows++;
                    }
                } while (await reader.NextResultAsync(ct));
            }
            stopwatch.Stop();
            if (plans.Count != 1 || !plans[0].Contains("ShowPlanXML", StringComparison.Ordinal))
                throw new InvalidOperationException("SQL Server did not return the expected actual execution plan.");
            var raw = messages.ToString();
            var io = Regex.Matches(raw, @"Table 'SqlTuningOrders'[^\r\n]*?logical reads (?<reads>\d+)", RegexOptions.CultureInvariant);
            var time = Regex.Matches(raw, @"SQL Server Execution Times:\s*CPU time = (?<cpu>\d+) ms,\s*elapsed time = (?<elapsed>\d+) ms", RegexOptions.CultureInvariant);
            var operators = XDocument.Parse(plans[0]).Descendants().Where(x => x.Name.LocalName == "RelOp")
                .Select(x => (string?)x.Attribute("PhysicalOp")).Where(x => x is not null).Cast<string>().Distinct().ToArray();
            return new(rows, Convert.ToHexString(hash.GetHashAndReset()), stopwatch.Elapsed.TotalMilliseconds,
                io.Count == 0 ? null : io.Sum(x => int.Parse(x.Groups["reads"].Value, CultureInfo.InvariantCulture)),
                time.Count == 0 ? null : time.Sum(x => int.Parse(x.Groups["cpu"].Value, CultureInfo.InvariantCulture)),
                time.Count == 0 ? null : time.Sum(x => int.Parse(x.Groups["elapsed"].Value, CultureInfo.InvariantCulture)),
                operators, plans[0], raw);
        }
        finally { connection.InfoMessage -= OnMessage; }
    }

    private static async Task<(Guid CustomerId, DateOnly Day, long TableCount)> ReadSampleAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = new SqlCommand("""
            SELECT TOP (1) CustomerId,CONVERT(date,CreatedAt) Day,COUNT_BIG(1) MatchingRows,
                (SELECT COUNT_BIG(1) FROM dbo.SqlTuningOrders) TableCount
            FROM dbo.SqlTuningOrders GROUP BY CustomerId,CONVERT(date,CreatedAt)
            ORDER BY COUNT_BIG(1) DESC,CustomerId,Day;
            """, connection) { CommandTimeout = 120 };
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("No benchmark rows found.");
        return (reader.GetGuid(0), DateOnly.FromDateTime(reader.GetDateTime(1)), reader.GetInt64(3));
    }

    private static async Task<(string ProductVersion, string Edition, int CompatibilityLevel)> ReadServerAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = new SqlCommand("""
            SELECT CONVERT(nvarchar(100),SERVERPROPERTY('ProductVersion')),
                   CONVERT(nvarchar(100),SERVERPROPERTY('Edition')),
                   compatibility_level FROM sys.databases WHERE name=DB_NAME();
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetString(0), reader.GetString(1), reader.GetByte(2));
    }

    private static async Task ExecuteAsync(SqlConnection connection, string query, CancellationToken ct)
    {
        await using var command = new SqlCommand(query, connection) { CommandTimeout = 300 };
        await command.ExecuteNonQueryAsync(ct);
    }

    private static double? Median(IEnumerable<int?> values)
    {
        var measured = values.Where(x => x.HasValue).Select(x => (double)x!.Value).Order().ToArray();
        return measured.Length == 0 ? null : measured[measured.Length / 2];
    }

    private sealed record Measurement(int Rows, string ResultSha256, double ClientElapsedMs, int? LogicalReads,
        int? SqlCpuMs, int? SqlElapsedMs, string[] PhysicalOperators, string PlanXml, string Statistics);
}
