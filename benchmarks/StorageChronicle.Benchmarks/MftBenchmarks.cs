using BenchmarkDotNet.Attributes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
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
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { WriteIndented = true };
    private WindowsMftEnumerator? enumerator;
    private IReadOnlyList<MftEntry> savedEntries = [];
    private Dictionary<FileId, (FileId? Parent, string Name, long LastUsn)> saved = new();
    private string devicePath = string.Empty;
    private string markerPath = string.Empty;
    private readonly Dictionary<string, Measurement> measurements = new(StringComparer.Ordinal);

    /// <summary>Requires a real Windows device path and snapshots its actual MFT result outside measurement.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The MFT benchmark requires Windows 10 22H2 or later.");
        devicePath = Environment.GetEnvironmentVariable("STORAGE_CHRONICLE_MFT_VOLUME") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(devicePath)) throw new InvalidOperationException("Set STORAGE_CHRONICLE_MFT_VOLUME to a dedicated NTFS device path such as \\\\.\\C: before running the MFT benchmark.");
        if (!string.Equals(Environment.GetEnvironmentVariable("STORAGE_CHRONICLE_MFT_VOLUME_LABEL"), "SC_TEST_MFT_VOLUME", StringComparison.Ordinal)) throw new InvalidOperationException("STORAGE_CHRONICLE_MFT_VOLUME_LABEL must be SC_TEST_MFT_VOLUME; host/system volumes are not accepted.");
        markerPath = Environment.GetEnvironmentVariable("STORAGE_CHRONICLE_MFT_MARKER_PATH") ?? string.Empty;
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
    public Task<int> MftEnumerationImport1M() => MeasureAsync("MftEnumerationImport1M", RequiredEntryCount, async () =>
    {
        var current = await EnumerateAsync().ConfigureAwait(false);
        if (current.Count < RequiredEntryCount) throw new InvalidOperationException($"The measured NTFS enumeration returned {current.Count:N0} entries; the 1M workload was not met.");
        var candidates = NtfsReconciliationComparer.Compare(current, saved);
        return new MeasurementResult(current.Count, candidates.Count, 0, 0, 0, candidates.Count);
    });

    /// <summary>Enumerates the first 10,000 entries of the real public MFT API.</summary>
    [Benchmark]
    public Task<int> MftEnumerationImport10K() => MeasureAsync("MftEnumerationImport10K", 10_000, async () =>
    {
        var current = await EnumerateAsync(10_000).ConfigureAwait(false);
        return new MeasurementResult(current.Count, 0, 0, 0, 0, current.Count);
    });

    /// <summary>Enumerates the first 100,000 entries of the real public MFT API.</summary>
    [Benchmark]
    public Task<int> MftEnumerationImport100K() => MeasureAsync("MftEnumerationImport100K", 100_000, async () =>
    {
        var current = await EnumerateAsync(100_000).ConfigureAwait(false);
        return new MeasurementResult(current.Count, 0, 0, 0, 0, current.Count);
    });

    /// <summary>Measures zero detailed metadata queries when the 1M-entry candidate comparison is unchanged.</summary>
    [Benchmark]
    public Task<int> MftCandidateMetadataQueriesZero1M() => MeasureAsync("MftCandidateMetadataQueriesZero1M", RequiredEntryCount, async () =>
    {
        var current = await EnumerateAsync().ConfigureAwait(false);
        var candidates = NtfsReconciliationComparer.Compare(current, saved);
        if (candidates.Count != 0) throw new InvalidOperationException($"The unchanged candidate oracle produced {candidates.Count} candidates.");
        return new MeasurementResult(current.Count, 0, 0, 0, 0, candidates.Count);
    });

    /// <summary>Measures a small candidate set without pretending to query metadata for non-candidates.</summary>
    [Benchmark]
    public Task<int> MftCandidateMetadataQueriesSmall1M() => MeasureAsync("MftCandidateMetadataQueriesSmall1M", RequiredEntryCount, async () =>
    {
        var current = await EnumerateAsync().ConfigureAwait(false);
        var candidateSaved = new Dictionary<FileId, (FileId? Parent, string Name, long LastUsn)>(saved);
        var first = current.Count == 0 ? throw new InvalidOperationException("The MFT enumeration returned no entries.") : current[0];
        candidateSaved[first.ToFileId()] = (first.ToParentFileId(), first.Name, first.Usn - 1);
        var candidates = NtfsReconciliationComparer.Compare(current, candidateSaved);
        if (candidates.Count is < 1 or > 1) throw new InvalidOperationException($"The small candidate oracle produced {candidates.Count} candidates.");
        return new MeasurementResult(current.Count, candidates.Count, 0, 0, 0, candidates.Count);
    });

    /// <summary>Writes the correctness counters consumed by the full matrix gate after all real benchmark methods complete.</summary>
    [GlobalCleanup]
    public void WriteCorrectnessEvidence()
    {
        var outputPath = Environment.GetEnvironmentVariable("STORAGE_CHRONICLE_MFT_EVIDENCE_PATH");
        if (string.IsNullOrWhiteSpace(outputPath)) throw new InvalidOperationException("STORAGE_CHRONICLE_MFT_EVIDENCE_PATH is required for the MFT correctness artifact.");

        var requiredMethods = new[]
        {
            "MftEnumerationImport10K",
            "MftEnumerationImport100K",
            "MftEnumerationImport1M",
            "MftCandidateMetadataQueriesZero1M",
            "MftCandidateMetadataQueriesSmall1M"
        };
        var complete = requiredMethods.All(measurements.ContainsKey);
        var environment = new
        {
            OperatingSystem = RuntimeInformation.OSDescription,
            OsBuild = Environment.OSVersion.Version.ToString(),
            VmCpuCount = RequiredEnvironment("STORAGE_CHRONICLE_MFT_VM_CPU_COUNT"),
            VmMemoryMiB = RequiredEnvironment("STORAGE_CHRONICLE_MFT_VM_MEMORY_MIB"),
            VhdxType = RequiredEnvironment("STORAGE_CHRONICLE_MFT_VHDX_TYPE"),
            VhdxSizeGiB = RequiredEnvironment("STORAGE_CHRONICLE_MFT_VHDX_SIZE_GIB"),
            DevicePath = devicePath,
            MarkerPath = markerPath,
            VolumeLabel = Environment.GetEnvironmentVariable("STORAGE_CHRONICLE_MFT_VOLUME_LABEL") ?? string.Empty
        };
        var runs = requiredMethods.Where(measurements.ContainsKey).Select(method => measurements[method]).ToArray();
        var oneMillion = measurements.TryGetValue("MftEnumerationImport1M", out var million) && million.DatasetEntryCount >= RequiredEntryCount && million.EnumeratedEntryCount >= RequiredEntryCount;
        var noDrops = runs.All(value => value.DroppedEventCount == 0);
        var eligible = complete && oneMillion && noDrops && runs.All(value => value.EnumeratedEntryCount >= value.DatasetEntryCount);
        var evidence = new
        {
            Schema = "StorageChronicle.MftBenchmarkEvidence.v1",
            Status = eligible ? "PASSED" : "FAILED",
            AcceptanceEligible = eligible,
            DatasetScenario = "real-public-mft-enumeration",
            Runs = runs,
            Environment = environment,
            FailureReasons = new[]
            {
                complete ? string.Empty : "One or more required MFT benchmark methods did not emit counters.",
                oneMillion ? string.Empty : "The 1M dataset/enumeration contract was not met.",
                noDrops ? string.Empty : "At least one MFT run reported dropped events."
            }.Where(value => value.Length > 0).ToArray(),
            GeneratedUtc = DateTimeOffset.UtcNow
        };
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("The MFT evidence path has no parent directory."));
        File.WriteAllText(fullPath, JsonSerializer.Serialize(evidence, EvidenceJsonOptions));
    }

    private async Task<int> MeasureAsync(string method, long datasetEntryCount, Func<Task<MeasurementResult>> operation)
    {
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var stopwatch = Stopwatch.StartNew();
        var result = await operation().ConfigureAwait(false);
        stopwatch.Stop();
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: false);
        measurements[method] = new Measurement(
            method,
            datasetEntryCount,
            result.EnumeratedEntryCount,
            result.CandidateCount,
            result.DetailedMetadataQueryCount,
            result.GeneratedCanonicalCount,
            result.DroppedEventCount,
            stopwatch.Elapsed.TotalMilliseconds,
            Math.Max(0, allocatedAfter - allocatedBefore));
        return result.ReturnValue;
    }

    private static string RequiredEnvironment(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"{name} is required for an acceptance-eligible MFT evidence artifact.");

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

    private sealed record Measurement(
        string Method,
        long DatasetEntryCount,
        long EnumeratedEntryCount,
        long CandidateCount,
        long DetailedMetadataQueryCount,
        long GeneratedCanonicalCount,
        long DroppedEventCount,
        double ElapsedMilliseconds,
        long AllocatedBytes);

    private sealed record MeasurementResult(
        long EnumeratedEntryCount,
        long CandidateCount,
        long DetailedMetadataQueryCount,
        long GeneratedCanonicalCount,
        long DroppedEventCount,
        int ReturnValue);
}
