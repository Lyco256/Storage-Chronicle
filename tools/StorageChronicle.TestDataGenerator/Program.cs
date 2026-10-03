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

        try
        {
            var safeOutputPath = outputPath is null ? null : ValidateNewOutputPath(outputPath);
            var records = Enumerable.Range(1, count).Select(sequence => new GeneratedRecord(sequence, sequence % 7 == 0 ? "MetadataChanged" : "DataWrite", $"file-{(sequence + seed) % Math.Max(1, count):D8}", sequence % 13 == 0 ? "ExistenceOnly" : "Exact"));
            if (string.Equals(format, "golden", StringComparison.OrdinalIgnoreCase))
            {
                var golden = new GoldenDocument("generated-v1", records.ToArray(), new GoldenExpected(count, count));
                Write(JsonSerializer.Serialize(golden, JsonOptions), safeOutputPath);
            }
            else
            {
                Write(string.Join(Environment.NewLine, records.Select(record => JsonSerializer.Serialize(record, JsonOptions))), safeOutputPath);
            }
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"output rejected: {exception.Message}");
            return 1;
        }
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

    private static string ValidateNewOutputPath(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("The output path must not be empty.", nameof(outputPath));
        if (outputPath.StartsWith("\\\\", StringComparison.Ordinal) || outputPath.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("Network and device paths are not accepted.", nameof(outputPath));
        var colonIndex = outputPath.IndexOf(':');
        if (colonIndex >= 0 && !(colonIndex == 1 && char.IsAsciiLetter(outputPath[0]) && outputPath.Length > 2 && outputPath[2] is '\\' or '/'))
            throw new ArgumentException("Drive-relative paths and alternate data streams are not accepted.", nameof(outputPath));

        var pathSegments = outputPath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (pathSegments.Any(segment => segment is "." or ".."))
            throw new ArgumentException("Relative traversal path segments are not accepted.", nameof(outputPath));

        var fullPath = Path.GetFullPath(outputPath);
        var parentPath = Path.GetDirectoryName(fullPath);
        var fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(fileName))
            throw new ArgumentException("The output must name a file inside an existing parent directory.", nameof(outputPath));

        var current = new DirectoryInfo(parentPath);
        if (!current.Exists) throw new DirectoryNotFoundException("The output parent directory must already exist.");
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Output paths through symbolic links, junctions, or other reparse points are not accepted.");
            current = current.Parent;
        }

        return fullPath;
    }

    private static void Write(string value, string? outputPath)
    {
        if (outputPath is null)
        {
            Console.WriteLine(value);
            return;
        }

        using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.WriteLine(value);
    }

    private sealed record GeneratedRecord(int Sequence, string Operation, string Name, string Quality);
    private sealed record GoldenDocument(string Id, IReadOnlyList<GeneratedRecord> Events, GoldenExpected Expected);
    private sealed record GoldenExpected(int SourceCount, int CanonicalCount);
}
