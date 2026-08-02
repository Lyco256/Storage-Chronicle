using System.Globalization;
using System.Text.Json;

namespace StorageChronicle.TestDataGenerator;

/// <summary>Generates deterministic metadata-only event fixtures for repeatable tests.</summary>
public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Writes newline-delimited event metadata, never file contents or hashes.</summary>
    public static int Main(string[] args)
    {
        if (!TryParse(args, out var count, out var outputPath, out var format, out var seed))
        {
            Console.Error.WriteLine("usage: StorageChronicle.TestDataGenerator --count <n> [--output <path>] [--format ndjson|golden] [--seed <n>]");
            return 2;
        }

        var records = Enumerable.Range(1, count).Select(sequence => new GeneratedRecord(sequence, sequence % 7 == 0 ? "MetadataChanged" : "DataWrite", $"file-{(sequence + seed) % Math.Max(1, count):D8}", sequence % 13 == 0 ? "ExistenceOnly" : "Exact"));
        if (string.Equals(format, "golden", StringComparison.OrdinalIgnoreCase))
        {
            var golden = new GoldenDocument("generated-v1", records.ToArray(), new GoldenExpected(count, count));
            Write(JsonSerializer.Serialize(golden, JsonOptions), outputPath);
        }
        else
        {
            Write(string.Join(Environment.NewLine, records.Select(record => JsonSerializer.Serialize(record, JsonOptions))), outputPath);
        }
        return 0;
    }

    private static bool TryParse(string[] args, out int count, out string? outputPath, out string format, out int seed)
    {
        count = 0; outputPath = null; format = "ndjson"; seed = 0;
        var countProvided = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--count" when ++index < args.Length && int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) && count is >= 0 and <= 5_000_000:
                    countProvided = true;
                    break;
                case "--output" when ++index < args.Length: outputPath = args[index]; break;
                case "--format" when ++index < args.Length && args[index] is "ndjson" or "golden": format = args[index]; break;
                case "--seed" when ++index < args.Length && int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out seed): break;
                default: return false;
            }
        }
        return countProvided;
    }

    private static void Write(string value, string? outputPath)
    {
        if (outputPath is null) Console.WriteLine(value);
        else File.WriteAllText(outputPath, value + Environment.NewLine);
    }

    private sealed record GeneratedRecord(int Sequence, string Operation, string Name, string Quality);
    private sealed record GoldenDocument(string Id, IReadOnlyList<GeneratedRecord> Events, GoldenExpected Expected);
    private sealed record GoldenExpected(int SourceCount, int CanonicalCount);
}
