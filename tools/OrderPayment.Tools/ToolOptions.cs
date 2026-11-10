namespace OrderPayment.Tools;

public sealed record ToolOptions(string Command, int Orders, string OutputDirectory)
{
    public static ToolOptions? Parse(string[] args)
    {
        if (args.Length == 0 || args is ["--help"] or ["-h"]) return null;
        var command = args[0];
        if (command is not ("seed" or "benchmark")) throw new ArgumentException("Expected seed or benchmark; use --help for syntax.");
        var orders = 100_000;
        var output = "artifacts/sql-tuning";
        var allowed = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            var flag = args[i];
            if (!seen.Add(flag)) throw new ArgumentException("Duplicate command-line option.");
            switch (flag)
            {
                case "--allow-demo-data" when command == "seed": allowed = true; break;
                case "--allow-benchmark" when command == "benchmark": allowed = true; break;
                case "--orders" when command == "seed":
                    if (++i == args.Length || !int.TryParse(args[i], out orders) || orders is < 1 or > 1_000_000)
                        throw new ArgumentException("--orders must be an integer from 1 to 1000000.");
                    break;
                case "--output" when command == "benchmark":
                    if (++i == args.Length || string.IsNullOrWhiteSpace(args[i])) throw new ArgumentException("--output needs a directory path.");
                    output = args[i];
                    break;
                default: throw new ArgumentException("Unknown or inapplicable option; use --help for syntax.");
            }
        }
        if (!allowed) throw new ArgumentException(command == "seed" ? "seed requires --allow-demo-data." : "benchmark requires --allow-benchmark.");
        return new(command, orders, output);
    }

    public void ValidateDatabase(string database)
    {
        var suffixes = Command == "seed" ? new[] { "Demo", "Benchmark", "Tests" } : ["Benchmark"];
        if (string.IsNullOrWhiteSpace(database) || !suffixes.Any(s => database.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException(Command == "seed"
                ? "Seed is restricted to an explicitly named database ending in Demo, Benchmark or Tests. Never target production."
                : "Benchmark is restricted to a database ending in Benchmark. Never target an application database.");
    }
}
