namespace StorageChronicle.LogInspector;

/// <summary>Performs bounded, metadata-only inspection of an agent log.</summary>
public static class Program
{
    /// <summary>Prints counts of known severity markers without retaining log contents.</summary>
    public static int Main(string[] args)
    {
        if (args.Length is < 1 or > 3 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("usage: StorageChronicle.LogInspector <log-path> [--json] [--max-lines <n>]");
            return 2;
        }

        var json = args.Any(value => value == "--json");
        var maxLines = ParseMaxLines(args);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var linesRead = 0;
        foreach (var line in File.ReadLines(args[0]))
        {
            if (++linesRead > maxLines) break;
            foreach (var marker in new[] { "TRACE", "DEBUG", "INFO", "WARN", "ERROR", "FATAL" })
            {
                if (line.Contains(marker, StringComparison.OrdinalIgnoreCase)) counts[marker] = counts.GetValueOrDefault(marker) + 1;
            }
        }
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { linesRead = Math.Min(linesRead, maxLines), truncated = linesRead > maxLines, counts }));
        else Console.WriteLine(string.Join(";", counts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}")));
        return 0;
    }

    private static int ParseMaxLines(string[] args)
    {
        var marker = Array.IndexOf(args, "--max-lines");
        return marker >= 0 && marker + 1 < args.Length && int.TryParse(args[marker + 1], out var value) && value > 0 ? value : 1_000_000;
    }
}
