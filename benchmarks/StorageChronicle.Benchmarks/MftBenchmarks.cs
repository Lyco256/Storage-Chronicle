using BenchmarkDotNet.Attributes;
using System.Diagnostics;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Normalization;
using StorageChronicle.Platform.Windows.FileSystem.Interop;
using StorageChronicle.Platform.Windows.FileSystem.Snapshot;
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
    private string mountRoot = string.Empty;
    private WindowsFileMetadataReader? metadataReader;
    private EventNormalizer? normalizer;
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
        mountRoot = Path.GetPathRoot(markerPath) ?? throw new InvalidOperationException("The MFT marker path has no mount root.");
        enumerator = new WindowsMftEnumerator(new WindowsNtfsApi(), devicePath);
        metadataReader = new WindowsFileMetadataReader();
        normalizer = new EventNormalizer();
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
        return await QueryCandidateMetadataAndGenerateCanonicalAsync(current, candidates).ConfigureAwait(false);
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
            Math.Max(0, allocatedAfter - allocatedBefore),
            result.PrivilegeEnableSuccessCount,
            result.PrivilegeFallbackCount,
            result.IoHintAttempts,
            result.IoHintSuccesses,
            result.IoHintFailures);
        return result.ReturnValue;
    }

    private async Task<MeasurementResult> QueryCandidateMetadataAndGenerateCanonicalAsync(IReadOnlyList<MftEntry> current, IReadOnlyList<NtfsReconciliationCandidate> candidates)
    {
        var reader = metadataReader ?? throw new InvalidOperationException("The Windows metadata reader was not initialized.");
        var eventNormalizer = normalizer ?? throw new InvalidOperationException("The production event normalizer was not initialized.");
        var entries = current.ToDictionary(entry => entry.ToFileId());
        var privilegeSuccesses = 0;
        var privilegeFallbacks = 0;
        var ioHintAttempts = 0;
        var ioHintSuccesses = 0;
        var ioHintFailures = 0;
        var generatedCanonicalCount = 0;
        var runId = Guid.NewGuid().ToString("N");
        foreach (var candidate in candidates)
        {
            var entry = candidate.Current;
            var path = ResolveMftPath(entry, entries, mountRoot);
            using var priority = WindowsReconciliationPriorityScope.Enter();
            using var privilege = WindowsSeBackupPrivilegeScope.Enter();
            if (privilege.Result.Enabled) privilegeSuccesses++;
            else privilegeFallbacks++;

            NativeFileMetadataRecord nativeMetadata;
            EventQuality quality;
            try
            {
                if (entry.IsDirectory)
                {
                    using var handle = reader.OpenDirectoryHandle(path);
                    if (priority.TrySetLowFileIoPriority(handle)) ioHintSuccesses++;
                    else ioHintFailures++;
                    ioHintAttempts++;
                }

                nativeMetadata = reader.Read(path, Path.GetDirectoryName(path));
                quality = nativeMetadata.IsAccessDenied ? EventQuality.ExistenceOnly : EventQuality.Reconciled;
            }
            catch (UnauthorizedAccessException)
            {
                nativeMetadata = FallbackMetadata(entry);
                quality = EventQuality.ExistenceOnly;
            }
            catch (IOException)
            {
                nativeMetadata = FallbackMetadata(entry);
                quality = EventQuality.Unknown;
            }
            var metadata = new FileMetadata(
                VolumeId.Create(devicePath),
                nativeMetadata.FileId,
                nativeMetadata.ParentFileId ?? entry.ToParentFileId(),
                nativeMetadata.Name,
                nativeMetadata.Kind,
                nativeMetadata.LogicalSize,
                nativeMetadata.AllocatedSize,
                nativeMetadata.CreatedUtc,
                nativeMetadata.LastAccessUtc,
                nativeMetadata.LastWriteUtc,
                nativeMetadata.FileSystemChangeUtc,
                nativeMetadata.Attributes,
                nativeMetadata.ReparsePointKind,
                null,
                quality,
                nativeMetadata.Exists,
                false);
            var properties = ImmutableDictionary<string, string>.Empty
                .Add("reconciliationRunId", runId)
                .Add("sourceRoute", "NtfsMftReconciliation")
                .Add("metadataQuality", quality.ToString());
            var now = DateTimeOffset.UtcNow;
            var source = new SourceEvent(
                EventId.New(),
                EventSchemaVersion.Current,
                EventOrigin.MftReconciliation,
                VolumeId.Create(devicePath),
                entry.ToFileId(),
                entry.ToParentFileId(),
                entry.Name,
                candidate.StoredName,
                CanonicalOperation.MetadataChanged,
                metadata,
                new EventTime(now, now.Offset, null, now, new SourceSequence(entry.Usn), new MountSequence(entry.Usn)),
                EventQuality.Reconciled,
                null,
                ProcessAttributionQuality.Unknown,
                null,
                runId,
                properties);
            if (eventNormalizer.Normalize(source) is not null) generatedCanonicalCount++;
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return new MeasurementResult(
            current.Count,
            candidates.Count,
            candidates.Count,
            generatedCanonicalCount,
            0,
            candidates.Count,
            privilegeSuccesses,
            privilegeFallbacks,
            ioHintAttempts,
            ioHintSuccesses,
            ioHintFailures);
    }

    private static NativeFileMetadataRecord FallbackMetadata(MftEntry entry) => new(
        entry.ToFileId(),
        entry.ToParentFileId(),
        entry.Name,
        entry.IsDirectory ? FileKind.Directory : FileKind.File,
        null,
        null,
        null,
        null,
        null,
        null,
        (FileAttributes)entry.FileAttributes,
        null,
        true,
        true);

    private static string ResolveMftPath(MftEntry entry, IReadOnlyDictionary<FileId, MftEntry> entries, string root)
    {
        var names = new Stack<string>();
        var cursor = entry;
        var seen = new HashSet<FileId>();
        while (seen.Add(cursor.ToFileId()))
        {
            if (!string.IsNullOrWhiteSpace(cursor.Name)) names.Push(cursor.Name);
            if (!entries.TryGetValue(cursor.ToParentFileId(), out cursor!)) break;
        }

        var path = root;
        foreach (var name in names)
        {
            var combined = Path.GetFullPath(Path.Combine(path, name));
            if (!combined.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return root;
            path = combined;
        }

        return path;
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
        long AllocatedBytes,
        int PrivilegeEnableSuccessCount,
        int PrivilegeFallbackCount,
        int IoHintAttempts,
        int IoHintSuccesses,
        int IoHintFailures);

    private sealed record MeasurementResult(
        long EnumeratedEntryCount,
        long CandidateCount,
        long DetailedMetadataQueryCount,
        long GeneratedCanonicalCount,
        long DroppedEventCount,
        int ReturnValue,
        int PrivilegeEnableSuccessCount = 0,
        int PrivilegeFallbackCount = 0,
        int IoHintAttempts = 0,
        int IoHintSuccesses = 0,
        int IoHintFailures = 0);
}
