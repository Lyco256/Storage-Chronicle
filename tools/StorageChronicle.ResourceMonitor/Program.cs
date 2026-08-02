using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace StorageChronicle.ResourceMonitor;

/// <summary>Samples a running process and applies the documented resource budget.</summary>
public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Runs a bounded measurement for a process selected by PID.</summary>
    public static int Main(string[] args)
    {
        if (!TryParse(args, out var options))
        {
            Console.Error.WriteLine("usage: StorageChronicle.ResourceMonitor --pid <n> [--duration-seconds <n>] [--interval-ms <n>] [--output <path>] [--max-private-mib <n>] [--max-cpu-percent <n>]");
            return 2;
        }

        try
        {
            using var process = Process.GetProcessById(options.ProcessId);
            var samples = Sample(process, options.Duration, options.IntervalMilliseconds);
            var result = BuildResult(options, samples);
            var json = JsonSerializer.Serialize(result, JsonOptions);
            if (options.OutputPath is null) Console.WriteLine(json);
            else File.WriteAllText(options.OutputPath, json + Environment.NewLine);

            return result.PrivateMemoryLimitExceeded || result.CpuLimitExceeded ? 1 : 0;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"process lookup failed: {exception.Message}");
            return 3;
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine($"measurement failed: {exception.Message}");
            return 4;
        }
    }

    private static IReadOnlyList<ResourceSample> Sample(Process process, TimeSpan duration, int intervalMilliseconds)
    {
        process.Refresh();
        var startCpu = process.TotalProcessorTime;
        var stopwatch = Stopwatch.StartNew();
        var samples = new List<ResourceSample>();
        while (stopwatch.Elapsed < duration)
        {
            Thread.Sleep(intervalMilliseconds);
            try
            {
                process.Refresh();
                samples.Add(new ResourceSample(DateTimeOffset.UtcNow, process.PrivateMemorySize64, process.WorkingSet64));
            }
            catch (InvalidOperationException)
            {
                break;
            }
        }

        var elapsed = stopwatch.Elapsed;
        if (samples.Count == 0) throw new InvalidOperationException("the process exited before a sample could be collected.");
        var cpu = process.HasExited ? startCpu : process.TotalProcessorTime;
        var cpuPercent = elapsed.TotalMilliseconds <= 0
            ? 0
            : (cpu - startCpu).TotalMilliseconds / (elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100;
        return samples.Select(sample => sample with { CpuPercent = cpuPercent }).ToArray();
    }

    private static ResourceResult BuildResult(MonitorOptions options, IReadOnlyList<ResourceSample> samples)
    {
        var peakPrivate = samples.Max(sample => sample.PrivateBytes);
        var averageCpu = samples.Average(sample => sample.CpuPercent);
        return new ResourceResult(
            options.ProcessId,
            samples.Count,
            peakPrivate,
            peakPrivate / 1024d / 1024d,
            averageCpu,
            peakPrivate / 1024d / 1024d > options.MaxPrivateMiB,
            averageCpu > options.MaxCpuPercent,
            "Disk-write accounting is intentionally reported by the host-specific collector; this portable monitor does not infer it.",
            samples);
    }

    private static bool TryParse(string[] args, out MonitorOptions options)
    {
        var pid = 0;
        var durationSeconds = 10;
        var intervalMilliseconds = 250;
        string? outputPath = null;
        var maxPrivateMiB = 50d;
        var maxCpuPercent = 0.5d;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--pid" when ++index < args.Length && int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) && pid > 0: break;
                case "--duration-seconds" when ++index < args.Length && int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out durationSeconds) && durationSeconds is >= 1 and <= 600: break;
                case "--interval-ms" when ++index < args.Length && int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out intervalMilliseconds) && intervalMilliseconds is >= 50 and <= 10_000: break;
                case "--output" when ++index < args.Length: outputPath = args[index]; break;
                case "--max-private-mib" when ++index < args.Length && double.TryParse(args[index], NumberStyles.Float, CultureInfo.InvariantCulture, out maxPrivateMiB) && maxPrivateMiB > 0: break;
                case "--max-cpu-percent" when ++index < args.Length && double.TryParse(args[index], NumberStyles.Float, CultureInfo.InvariantCulture, out maxCpuPercent) && maxCpuPercent > 0: break;
                default: options = default; return false;
            }
        }

        options = new MonitorOptions(pid, TimeSpan.FromSeconds(durationSeconds), intervalMilliseconds, outputPath, maxPrivateMiB, maxCpuPercent);
        return pid > 0;
    }

    private readonly record struct MonitorOptions(int ProcessId, TimeSpan Duration, int IntervalMilliseconds, string? OutputPath, double MaxPrivateMiB, double MaxCpuPercent);
    private sealed record ResourceSample(DateTimeOffset RecordedUtc, long PrivateBytes, long WorkingSetBytes, double CpuPercent = 0);
    private sealed record ResourceResult(int ProcessId, int SampleCount, long PeakPrivateBytes, double PeakPrivateMiB, double AverageCpuPercent, bool PrivateMemoryLimitExceeded, bool CpuLimitExceeded, string DiskWriteNote, IReadOnlyList<ResourceSample> Samples);
}
