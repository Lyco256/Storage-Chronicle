using System.Globalization;

namespace StorageChronicle.TestDataGenerator;

/// <summary>Generates deterministic metadata-only event fixtures for repeatable tests.</summary>
public static class Program
{
    /// <summary>Writes newline-delimited event metadata, never file contents or hashes.</summary>
    public static int Main(string[] args)
    {
        if (args.Length is < 1 or > 2 || !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 0)
        {
            Console.Error.WriteLine("usage: StorageChronicle.TestDataGenerator <count> [output-path]");
            return 2;
        }

        var lines = Enumerable.Range(1, count).Select(sequence => $"{{\"sequence\":{sequence},\"operation\":\"DataWrite\",\"quality\":\"Exact\"}}");
        if (args.Length == 2) File.WriteAllLines(args[1], lines);
        else foreach (var line in lines) Console.WriteLine(line);
        return 0;
    }
}
