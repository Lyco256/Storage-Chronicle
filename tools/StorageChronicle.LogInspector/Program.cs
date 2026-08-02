namespace StorageChronicle.LogInspector;

/// <summary>Performs bounded, metadata-only inspection of an agent log.</summary>
public static class Program
{
    /// <summary>Prints counts of known severity markers without retaining log contents.</summary>
    public static int Main(string[] args)
    {
        if (args.Length != 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("usage: StorageChronicle.LogInspector <log-path>");
            return 2;
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(args[0]))
        {
            foreach (var marker in new[] { "TRACE", "DEBUG", "INFO", "WARN", "ERROR", "FATAL" })
            {
                if (line.Contains(marker, StringComparison.OrdinalIgnoreCase)) counts[marker] = counts.GetValueOrDefault(marker) + 1;
            }
        }
        Console.WriteLine(string.Join(";", counts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}")));
        return 0;
    }
}
