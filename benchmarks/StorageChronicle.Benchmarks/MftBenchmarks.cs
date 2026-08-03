using BenchmarkDotNet.Attributes;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Windows.Ntfs;

namespace StorageChronicle.Benchmarks;

/// <summary>Measures actual public FSCTL_ENUM_USN_DATA enumeration and reconciliation on a supplied NTFS volume.</summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
[InvocationCount(1)]
[IterationCount(1)]
[WarmupCount(0)]
[BenchmarkCategory("WindowsPrivileged", "MFT")]
public class WindowsMftBenchmarks
{
    private const int RequiredEntryCount = BenchmarkFixtures.FileCount1M;
    private WindowsMftEnumerator? enumerator;
    private IReadOnlyList<MftEntry> savedEntries = [];
    private Dictionary<FileId, (FileId? Parent, string Name, long LastUsn)> saved = new();
    private string devicePath = string.Empty;

    /// <summary>Requires a real Windows device path and snapshots its actual MFT result outside measurement.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The MFT benchmark requires Windows 10 22H2 or later.");
        devicePath = Environment.GetEnvironmentVariable("STORAGE_CHRONICLE_MFT_VOLUME") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(devicePath)) throw new InvalidOperationException("Set STORAGE_CHRONICLE_MFT_VOLUME to a dedicated NTFS device path such as \\\\.\\C: before running the MFT benchmark.");
        enumerator = new WindowsMftEnumerator(new WindowsNtfsApi(), devicePath);
        savedEntries = await EnumerateAsync().ConfigureAwait(false);
        if (savedEntries.Count < RequiredEntryCount) throw new InvalidOperationException($"The supplied NTFS volume returned {savedEntries.Count:N0} MFT entries; at least {RequiredEntryCount:N0} are required for the 1M benchmark.");
        saved = new Dictionary<FileId, (FileId? Parent, string Name, long LastUsn)>(savedEntries.Count);
        foreach (var entry in savedEntries) saved[entry.ToFileId()] = (entry.ToParentFileId(), entry.Name, entry.Usn);
    }

    /// <summary>Enumerates the real MFT and runs the production reconciliation comparer over its result.</summary>
    [Benchmark]
    public async Task<int> MftEnumerationImport1M()
    {
        var current = await EnumerateAsync().ConfigureAwait(false);
        if (current.Count < RequiredEntryCount) throw new InvalidOperationException($"The measured NTFS enumeration returned {current.Count:N0} entries; the 1M workload was not met.");
        return NtfsReconciliationComparer.Compare(current, saved).Count;
    }

    private async Task<IReadOnlyList<MftEntry>> EnumerateAsync()
    {
        var currentEnumerator = enumerator ?? throw new InvalidOperationException("The MFT enumerator was not initialized.");
        var entries = new List<MftEntry>(RequiredEntryCount);
        await foreach (var entry in currentEnumerator.EnumerateAsync().ConfigureAwait(false)) entries.Add(entry);
        return entries;
    }
}
