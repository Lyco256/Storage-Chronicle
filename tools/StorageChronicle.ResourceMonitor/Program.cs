using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
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
            using var processes = new ProcessSet(options.ProcessIds);
            var samples = Sample(processes, options.Duration, options.IntervalMilliseconds);
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

    private static IReadOnlyList<ResourceSample> Sample(ProcessSet processes, TimeSpan duration, int intervalMilliseconds)
    {
        processes.Refresh();
        var startCpu = processes.TotalProcessorTime;
        var stopwatch = Stopwatch.StartNew();
        var samples = new List<ResourceSample>();
        while (stopwatch.Elapsed < duration)
        {
            Thread.Sleep(intervalMilliseconds);
            try
            {
                processes.Refresh();
                if (processes.PrivateWorkingSetBytes is not { } privateWorkingSet)
                {
                    throw new InvalidOperationException("Private working set is unavailable for one or more processes.");
                }

                samples.Add(new ResourceSample(DateTimeOffset.UtcNow, privateWorkingSet, processes.WorkingSetBytes, processes.WriteBytes));
            }
            catch (InvalidOperationException)
            {
                break;
            }
        }

        var elapsed = stopwatch.Elapsed;
        if (samples.Count == 0) throw new InvalidOperationException("the process exited before a sample could be collected.");
        var cpu = processes.TotalProcessorTime;
        var cpuPercent = elapsed.TotalMilliseconds <= 0
            ? 0
            : (cpu - startCpu).TotalMilliseconds / (elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100;
        return samples.Select(sample => sample with { CpuPercent = cpuPercent }).ToArray();
    }

    private static ResourceResult BuildResult(MonitorOptions options, IReadOnlyList<ResourceSample> samples)
    {
        var peakPrivateWorkingSet = samples.Max(sample => sample.PrivateWorkingSetBytes);
        var peakWorkingSet = samples.Max(sample => sample.WorkingSetBytes);
        var averageCpu = samples.Average(sample => sample.CpuPercent);
        var writes = samples.Where(sample => sample.WriteBytes is not null).Select(sample => sample.WriteBytes!.Value).ToArray();
        long? diskWriteBytes = writes.Length < 2 ? null : Math.Max(0, writes[^1] - writes[0]);
        return new ResourceResult(
            options.ProcessIds,
            samples.Count,
            peakPrivateWorkingSet,
            peakPrivateWorkingSet / 1024d / 1024d,
            peakWorkingSet,
            peakWorkingSet / 1024d / 1024d,
            averageCpu,
            peakPrivateWorkingSet / 1024d / 1024d > options.MaxPrivateMiB,
            averageCpu > options.MaxCpuPercent,
            diskWriteBytes,
            "Queue depth is reported by the Agent health IPC surface; this process monitor does not infer it from OS thread counts.",
            samples);
    }

    private static bool TryParse(string[] args, out MonitorOptions options)
    {
        var processIds = new List<int>();
        var durationSeconds = 10;
        var intervalMilliseconds = 250;
        string? outputPath = null;
        var maxPrivateMiB = 50d;
        var maxCpuPercent = 0.5d;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--pid" when ++index < args.Length && int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) && pid > 0: processIds.Add(pid); break;
                case "--duration-seconds" when ++index < args.Length && int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out durationSeconds) && durationSeconds is >= 1 and <= 600: break;
                case "--interval-ms" when ++index < args.Length && int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out intervalMilliseconds) && intervalMilliseconds is >= 50 and <= 10_000: break;
                case "--output" when ++index < args.Length: outputPath = args[index]; break;
                case "--max-private-mib" when ++index < args.Length && double.TryParse(args[index], NumberStyles.Float, CultureInfo.InvariantCulture, out maxPrivateMiB) && maxPrivateMiB > 0: break;
                case "--max-cpu-percent" when ++index < args.Length && double.TryParse(args[index], NumberStyles.Float, CultureInfo.InvariantCulture, out maxCpuPercent) && maxCpuPercent > 0: break;
                default: options = default; return false;
            }
        }

        options = new MonitorOptions(processIds, TimeSpan.FromSeconds(durationSeconds), intervalMilliseconds, outputPath, maxPrivateMiB, maxCpuPercent);
        return processIds.Count > 0;
    }

    private sealed class ProcessSet : IDisposable
    {
        private readonly IReadOnlyList<Process> processes;

        public ProcessSet(IReadOnlyList<int> processIds) => processes = processIds.Select(Process.GetProcessById).ToArray();

        public long WorkingSetBytes => processes.Sum(process => process.WorkingSet64);
        public long? PrivateWorkingSetBytes => OperatingSystem.IsWindows() ? TryGetPrivateWorkingSetBytes() : null;
        public long? WriteBytes => OperatingSystem.IsWindows() ? TryGetWriteBytes() : null;
        public TimeSpan TotalProcessorTime => processes.Aggregate(TimeSpan.Zero, (total, process) => total + process.TotalProcessorTime);
        public void Refresh()
        {
            foreach (var process in processes) process.Refresh();
        }

        public void Dispose()
        {
            foreach (var process in processes) process.Dispose();
        }

        private long? TryGetWriteBytes()
        {
            ulong total = 0;
            foreach (var process in processes)
            {
                try
                {
                    if (!GetProcessIoCounters(process.Handle, out var counters)) return null;
                    total = checked(total + counters.WriteTransferCount);
                }
                catch (InvalidOperationException) { return null; }
                catch (Win32Exception) { return null; }
                catch (UnauthorizedAccessException) { return null; }
            }

            return total > long.MaxValue ? long.MaxValue : (long)total;
        }

        private long? TryGetPrivateWorkingSetBytes()
        {
            ulong total = 0;
            foreach (var process in processes)
            {
                try
                {
                    var counters = new ProcessMemoryCountersEx2 { Size = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>() };
                    if (!GetProcessMemoryInfo(process.Handle, ref counters, counters.Size)) return null;
                    total = checked(total + counters.PrivateWorkingSetSize.ToUInt64());
                }
                catch (InvalidOperationException) { return null; }
                catch (Win32Exception) { return null; }
                catch (UnauthorizedAccessException) { return null; }
            }

            return total > long.MaxValue ? long.MaxValue : (long)total;
        }
    }

    private readonly record struct MonitorOptions(IReadOnlyList<int> ProcessIds, TimeSpan Duration, int IntervalMilliseconds, string? OutputPath, double MaxPrivateMiB, double MaxCpuPercent);
    private sealed record ResourceSample(DateTimeOffset RecordedUtc, long PrivateWorkingSetBytes, long WorkingSetBytes, long? WriteBytes, double CpuPercent = 0);
    private sealed record ResourceResult(IReadOnlyList<int> ProcessIds, int SampleCount, long PeakPrivateWorkingSetBytes, double PeakPrivateWorkingSetMiB, long PeakWorkingSetBytes, double PeakWorkingSetMiB, double AverageCpuPercent, bool PrivateMemoryLimitExceeded, bool CpuLimitExceeded, long? DiskWriteBytes, string QueueDepthNote, IReadOnlyList<ResourceSample> Samples);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx2
    {
        public uint Size;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivateUsage;
        public UIntPtr PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(IntPtr processHandle, ref ProcessMemoryCountersEx2 counters, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr processHandle, out IoCounters counters);
}
