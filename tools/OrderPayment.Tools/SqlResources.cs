using System.Reflection;

namespace OrderPayment.Tools;

internal static class SqlResources
{
    public static string Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"OrderPayment.Tools.Sql.{name}")
            ?? throw new InvalidOperationException("Embedded SQL resource not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
