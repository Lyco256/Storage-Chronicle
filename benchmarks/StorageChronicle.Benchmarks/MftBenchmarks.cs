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
        if (!string.Equals(Environment.GetEnvironmentVariable("STORAGE_CHRONICLE_MFT_VOLUME_LABEL"), "SC_TEST_MFT_VOLUME", StringComparison.Ordinal)) throw new InvalidOperationException("STORAGE_CHRONICLE_MFT_VOLUME_LABEL must be SC_TEST_MFT_VOLUME; host/system volumes are not accepted.");
        var markerPath = Environment.GetEnvironmentVariable("STORAGE_CHRONICLE_MFT_MARKER_PATH") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(markerPath) || !File.Exists(markerPath)) throw new InvalidOperationException("STORAGE_CHRONICLE_MFT_MARKER_PATH must point to the user-approved TestLab marker on the dedicated MFT data volume.");
        if (devicePath.Contains("C:", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The MFT benchmark refuses C: and host/system volumes.");
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

    /// <summary>Enumerates the first 10,000 entries of the real public MFT API.</summary>
    [Benchmark]
    public async Task<int> MftEnumerationImport10K() => (await EnumerateAsync(10_000).ConfigureAwait(false)).Count;

    /// <summary>Enumerates the first 100,000 entries of the real public MFT API.</summary>
    [Benchmark]
    public async Task<int> MftEnumerationImport100K() => (await EnumerateAsync(100_000).ConfigureAwait(false)).Count;

    /// <summary>Measures zero detailed metadata queries when the 1M-entry candidate comparison is unchanged.</summary>
    [Benchmark]
    public async Task<int> MftCandidateMetadataQueriesZero1M()
    {
        var current = await EnumerateAsync().ConfigureAwait(false);
        var candidates = NtfsReconciliationComparer.Compare(current, saved);
        if (candidates.Count != 0) throw new InvalidOperationException($"The unchanged candidate oracle produced {candidates.Count} candidates.");
        return candidates.Count;
    }

    /// <summary>Measures a small candidate set without pretending to query metadata for non-candidates.</summary>
    [Benchmark]
    public async Task<int> MftCandidateMetadataQueriesSmall1M()
    {
        var current = await EnumerateAsync().ConfigureAwait(false);
        var candidateSaved = new Dictionary<FileId, (FileId? Parent, string Name, long LastUsn)>(saved);
        var first = current.Count == 0 ? throw new InvalidOperationException("The MFT enumeration returned no entries.") : current[0];
        candidateSaved[first.ToFileId()] = (first.ToParentFileId(), first.Name, first.Usn - 1);
        var candidates = NtfsReconciliationComparer.Compare(current, candidateSaved);
        if (candidates.Count is < 1 or > 1) throw new InvalidOperationException($"The small candidate oracle produced {candidates.Count} candidates.");
        return candidates.Count;
    }

    private async Task<IReadOnlyList<MftEntry>> EnumerateAsync(int maximumCount = int.MaxValue)
    {
        var currentEnumerator = enumerator ?? throw new InvalidOperationException("The MFT enumerator was not initialized.");
        var entries = new List<MftEntry>(Math.Min(RequiredEntryCount, maximumCount));
        await foreach (var entry in currentEnumerator.EnumerateAsync().ConfigureAwait(false))
        {
            entries.Add(entry);
            if (entries.Count >= maximumCount) break;
        }
        return entries;
    }
}
