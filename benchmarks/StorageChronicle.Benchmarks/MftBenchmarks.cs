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
    private bool physicalSeedPreflightValidated;
    private WindowsFileMetadataReader? metadataReader;
    private EventNormalizer? normalizer;
    private readonly Dictionary<string, Measurement> measurements = new(StringComparer.Ordinal);

    /// <summary>Requires a real Windows device path and snapshots its actual MFT result outside measurement.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The MFT benchmark requires Windows 10 22H2 or later.");
        devicePath = Environment.GetEnvironmentVariable("STORAGE_CHRONICLE_MFT_VOLUME") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(devicePath)) throw new InvalidOperationException("Set STORAGE_CHRONICLE_MFT_VOLUME to the verified dedicated NTFS seed device path.");
        markerPath = Environment.GetEnvironmentVariable("STORAGE_CHRONICLE_MFT_MARKER_PATH") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(markerPath) || !File.Exists(markerPath)) throw new InvalidOperationException("STORAGE_CHRONICLE_MFT_MARKER_PATH must point to the persistent, preflight-verified seed marker.");
        ValidateEvidenceOutputTarget();
        ValidatePhysicalSeedPreflight();
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
            OperatingSystem = RequiredEnvironment("STORAGE_CHRONICLE_MFT_PHYSICAL_HOST_OS"),
            OsBuild = RequiredEnvironment("STORAGE_CHRONICLE_MFT_PHYSICAL_HOST_OS_BUILD"),
            IsPhysicalMachine = physicalSeedPreflightValidated,
            CpuName = RequiredEnvironment("STORAGE_CHRONICLE_MFT_PHYSICAL_HOST_CPU_NAME"),
            CpuLogicalCount = int.Parse(RequiredEnvironment("STORAGE_CHRONICLE_MFT_PHYSICAL_HOST_CPU_COUNT"), System.Globalization.CultureInfo.InvariantCulture),
            MemoryMiB = long.Parse(RequiredEnvironment("STORAGE_CHRONICLE_MFT_PHYSICAL_HOST_MEMORY_MIB"), System.Globalization.CultureInfo.InvariantCulture),
            VhdxPath = RequiredEnvironment("STORAGE_CHRONICLE_MFT_VHDX_PATH"),
            VhdxType = RequiredEnvironment("STORAGE_CHRONICLE_MFT_VHDX_TYPE"),
            VhdxSizeGiB = RequiredEnvironment("STORAGE_CHRONICLE_MFT_VHDX_SIZE_GIB"),
            DiskNumber = int.Parse(RequiredEnvironment("STORAGE_CHRONICLE_MFT_DISK_NUMBER"), System.Globalization.CultureInfo.InvariantCulture),
            DiskUniqueId = RequiredEnvironment("STORAGE_CHRONICLE_MFT_DISK_UNIQUE_ID"),
            VolumeUniqueId = RequiredEnvironment("STORAGE_CHRONICLE_MFT_VOLUME_UNIQUE_ID"),
            VolumeGuidPath = RequiredEnvironment("STORAGE_CHRONICLE_MFT_VOLUME_GUID_PATH"),
            SeedRunId = RequiredEnvironment("STORAGE_CHRONICLE_MFT_SEED_RUN_ID"),
            RunId = RequiredEnvironment("STORAGE_CHRONICLE_MFT_SEED_RUN_ID"),
            DatasetEntryCount = long.Parse(RequiredEnvironment("STORAGE_CHRONICLE_MFT_SEED_ENTRY_COUNT"), System.Globalization.CultureInfo.InvariantCulture),
            DevicePath = devicePath,
            MarkerPath = markerPath,
            VolumeLabel = "SC_TEST_MFT_VOLUME",
            PreflightSchema = RequiredEnvironment("STORAGE_CHRONICLE_MFT_PREFLIGHT_SCHEMA"),
            PreflightPath = RequiredEnvironment("STORAGE_CHRONICLE_MFT_PREFLIGHT_PATH"),
            SeedVhdxFileIdentity = RequiredEnvironment("STORAGE_CHRONICLE_MFT_SEED_VHDX_FILE_IDENTITY"),
            SeedWorkloadRoot = Path.GetFullPath(RequiredEnvironment("STORAGE_CHRONICLE_MFT_SEED_WORKLOAD_ROOT")),
            SeedWorkloadOraclePath = Path.GetFullPath(RequiredEnvironment("STORAGE_CHRONICLE_MFT_SEED_WORKLOAD_ORACLE_PATH")),
            SeedCreationEvidencePath = Path.GetFullPath(RequiredEnvironment("STORAGE_CHRONICLE_MFT_CREATION_EVIDENCE_PATH")),
            SeedCreationEvidenceSha256 = RequiredEnvironment("STORAGE_CHRONICLE_MFT_CREATION_EVIDENCE_SHA256"),
            SeedWriteMonitorEvidencePath = Path.GetFullPath(RequiredEnvironment("STORAGE_CHRONICLE_MFT_WRITE_MONITOR_EVIDENCE_PATH")),
            SeedWriteMonitorEvidenceSha256 = RequiredEnvironment("STORAGE_CHRONICLE_MFT_WRITE_MONITOR_EVIDENCE_SHA256"),
            SeedProcessIdentities = JsonSerializer.Deserialize<JsonElement>(RequiredEnvironment("STORAGE_CHRONICLE_MFT_SEED_PROCESS_IDENTITIES"))
        };
        var runs = requiredMethods.Where(measurements.ContainsKey).Select(method => measurements[method]).ToArray();
        var oneMillion = measurements.TryGetValue("MftEnumerationImport1M", out var million) && million.DatasetEntryCount >= RequiredEntryCount && million.EnumeratedEntryCount >= RequiredEntryCount;
        var noDrops = runs.All(value => value.DroppedEventCount == 0);
        var eligible = physicalSeedPreflightValidated && complete && oneMillion && noDrops && runs.All(value => value.EnumeratedEntryCount >= value.DatasetEntryCount);
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
                physicalSeedPreflightValidated ? string.Empty : "The physical MFT seed preflight was not validated.",
                complete ? string.Empty : "One or more required MFT benchmark methods did not emit counters.",
                oneMillion ? string.Empty : "The 1M dataset/enumeration contract was not met.",
                noDrops ? string.Empty : "At least one MFT run reported dropped events."
            }.Where(value => value.Length > 0).ToArray(),
            GeneratedUtc = DateTimeOffset.UtcNow
        };
        var fullPath = ValidateEvidenceOutputTarget();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(evidence, EvidenceJsonOptions);
        using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static string ValidateEvidenceOutputTarget()
    {
        var output = Path.GetFullPath(RequiredEnvironment("STORAGE_CHRONICLE_MFT_EVIDENCE_PATH"));
        var artifactRoot = Path.GetFullPath(RequiredEnvironment("STORAGE_CHRONICLE_MFT_ARTIFACT_ROOT"));
        if (!Directory.Exists(artifactRoot)) throw new InvalidOperationException("The externally approved MFT artifact root must already exist.");
        var parent = Path.GetDirectoryName(output) ?? throw new InvalidOperationException("The MFT evidence path has no parent directory.");
        if (!Directory.Exists(parent)) throw new InvalidOperationException("The MFT evidence parent must already exist; arbitrary parent creation is forbidden.");
        var relative = Path.GetRelativePath(artifactRoot, output);
        if (Path.IsPathRooted(relative) || relative.Equals("..", StringComparison.Ordinal) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("The MFT evidence output must remain under the externally approved ArtifactRoot.");
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("The MFT correctness evidence target already exists; refusing replacement.");
        return output;
    }

    private void ValidatePhysicalSeedPreflight()
    {
        var path = RequiredEnvironment("STORAGE_CHRONICLE_MFT_PREFLIGHT_PATH");
        if (!string.Equals(RequiredEnvironment("STORAGE_CHRONICLE_MFT_PREFLIGHT_SCHEMA"), "StorageChronicle.MftPhysicalSeedPreflight.v1", StringComparison.Ordinal))
            throw new InvalidOperationException("The MFT preflight schema is not supported.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.GetProperty("Schema").GetString() != "StorageChronicle.MftPhysicalSeedPreflight.v1" ||
            root.GetProperty("Status").GetString() != "PASS" ||
            !root.GetProperty("AcceptanceEligible").GetBoolean())
            throw new InvalidOperationException("The physical MFT seed preflight is not acceptance-eligible.");
        var environment = root.GetProperty("Environment");
        if (!environment.GetProperty("IsPhysicalMachine").GetBoolean() ||
            environment.GetProperty("DevicePath").GetString() != devicePath ||
            environment.GetProperty("MarkerPath").GetString() != Path.GetFullPath(markerPath) ||
            environment.GetProperty("VolumeLabel").GetString() != "SC_TEST_MFT_VOLUME" ||
            environment.GetProperty("VhdxType").GetString() != "Dynamic" ||
            environment.GetProperty("DatasetEntryCount").GetInt64() < RequiredEntryCount ||
            environment.GetProperty("VhdxPath").GetString() != RequiredEnvironment("STORAGE_CHRONICLE_MFT_VHDX_PATH") ||
            environment.GetProperty("DiskNumber").GetInt32() != int.Parse(RequiredEnvironment("STORAGE_CHRONICLE_MFT_DISK_NUMBER"), System.Globalization.CultureInfo.InvariantCulture) ||
            environment.GetProperty("DiskUniqueId").GetString() != RequiredEnvironment("STORAGE_CHRONICLE_MFT_DISK_UNIQUE_ID") ||
            environment.GetProperty("VolumeUniqueId").GetString() != RequiredEnvironment("STORAGE_CHRONICLE_MFT_VOLUME_UNIQUE_ID") ||
            environment.GetProperty("VolumeGuidPath").GetString() != RequiredEnvironment("STORAGE_CHRONICLE_MFT_VOLUME_GUID_PATH") ||
            environment.GetProperty("RunId").GetString() != RequiredEnvironment("STORAGE_CHRONICLE_MFT_SEED_RUN_ID"))
            throw new InvalidOperationException("The physical MFT preflight identity does not match the benchmark process configuration.");
        RequireEqual(environment, "VhdxFileIdentity", RequiredEnvironment("STORAGE_CHRONICLE_MFT_SEED_VHDX_FILE_IDENTITY"));
        RequireEqual(environment, "WorkloadRoot", Path.GetFullPath(RequiredEnvironment("STORAGE_CHRONICLE_MFT_SEED_WORKLOAD_ROOT")));
        var creationPath = Path.GetFullPath(RequiredEnvironment("STORAGE_CHRONICLE_MFT_CREATION_EVIDENCE_PATH"));
        var monitorPath = Path.GetFullPath(RequiredEnvironment("STORAGE_CHRONICLE_MFT_WRITE_MONITOR_EVIDENCE_PATH"));
        if (Sha256File(creationPath) != RequiredEnvironment("STORAGE_CHRONICLE_MFT_CREATION_EVIDENCE_SHA256") ||
            Sha256File(monitorPath) != RequiredEnvironment("STORAGE_CHRONICLE_MFT_WRITE_MONITOR_EVIDENCE_SHA256"))
            throw new InvalidOperationException("Seed creation or independent monitor evidence hash changed after matrix preflight.");
        using var creationDocument = JsonDocument.Parse(File.ReadAllText(creationPath));
        var creation = creationDocument.RootElement;
        if (creation.GetProperty("Schema").GetString() != "StorageChronicle.MftSeedCreationEvidence.v1" ||
            creation.GetProperty("Status").GetString() != "SEEDED" ||
            creation.GetProperty("RunId").GetString() != RequiredEnvironment("STORAGE_CHRONICLE_MFT_SEED_RUN_ID") ||
            creation.GetProperty("VhdxFileIdentity").GetString() != RequiredEnvironment("STORAGE_CHRONICLE_MFT_SEED_VHDX_FILE_IDENTITY") ||
            creation.GetProperty("VhdxPath").GetString() != RequiredEnvironment("STORAGE_CHRONICLE_MFT_VHDX_PATH") ||
            creation.GetProperty("WorkloadRoot").GetString() != Path.GetFullPath(RequiredEnvironment("STORAGE_CHRONICLE_MFT_SEED_WORKLOAD_ROOT")) ||
            creation.GetProperty("WorkloadOraclePath").GetString() != Path.GetFullPath(RequiredEnvironment("STORAGE_CHRONICLE_MFT_SEED_WORKLOAD_ORACLE_PATH")) ||
            creation.GetProperty("DiskUniqueId").GetString() != RequiredEnvironment("STORAGE_CHRONICLE_MFT_DISK_UNIQUE_ID") ||
            creation.GetProperty("VolumeUniqueId").GetString() != RequiredEnvironment("STORAGE_CHRONICLE_MFT_VOLUME_UNIQUE_ID"))
            throw new InvalidOperationException("Seed creation evidence does not match the benchmark's verified target identities.");
        using var suppliedProcesses = JsonDocument.Parse(RequiredEnvironment("STORAGE_CHRONICLE_MFT_SEED_PROCESS_IDENTITIES"));
        var recordedProcesses = creation.GetProperty("Processes");
        var suppliedProcessArray = suppliedProcesses.RootElement;
        if (suppliedProcessArray.ValueKind != JsonValueKind.Array || suppliedProcessArray.GetArrayLength() != recordedProcesses.GetArrayLength())
            throw new InvalidOperationException("Creator/publisher/workload process identities are incomplete.");
        foreach (var recordedProcess in recordedProcesses.EnumerateArray())
        {
            var role = recordedProcess.GetProperty("Role").GetString();
            var matching = suppliedProcessArray.EnumerateArray().Where(process => process.GetProperty("Role").GetString() == role).ToArray();
            if (matching.Length != 1 || matching[0].GetProperty("ProcessId").GetInt32() != recordedProcess.GetProperty("ProcessId").GetInt32() ||
                matching[0].GetProperty("StartTimeUtc").GetString() != recordedProcess.GetProperty("StartTimeUtc").GetString() ||
                matching[0].GetProperty("Sha256").GetString() != recordedProcess.GetProperty("Sha256").GetString())
                throw new InvalidOperationException($"Supplied process identity does not match creator evidence for {role}.");
        }
        using var monitorDocument = JsonDocument.Parse(File.ReadAllText(monitorPath));
        var monitor = monitorDocument.RootElement;
        if (monitor.GetProperty("Schema").GetString() != "StorageChronicle.MftSeedWriteMonitorEvidence.v1" ||
            monitor.GetProperty("Status").GetString() != "PASS" ||
            monitor.GetProperty("RunId").GetString() != RequiredEnvironment("STORAGE_CHRONICLE_MFT_SEED_RUN_ID") ||
            !monitor.GetProperty("Collection").GetProperty("TraceFinalized").GetBoolean() ||
            !monitor.GetProperty("Collection").GetProperty("CaptureComplete").GetBoolean() ||
            monitor.GetProperty("Collection").GetProperty("LostEventCount").GetInt64() != 0 ||
            monitor.GetProperty("Collection").GetProperty("DroppedEventCount").GetInt64() != 0)
            throw new InvalidOperationException("Independent write-monitor evidence is incomplete or does not match the seed run.");
        var monitorTarget = monitor.GetProperty("Target");
        if (monitorTarget.GetProperty("VhdxPath").GetString() != RequiredEnvironment("STORAGE_CHRONICLE_MFT_VHDX_PATH") ||
            monitorTarget.GetProperty("DiskUniqueId").GetString() != RequiredEnvironment("STORAGE_CHRONICLE_MFT_DISK_UNIQUE_ID") ||
            monitorTarget.GetProperty("VolumeUniqueId").GetString() != RequiredEnvironment("STORAGE_CHRONICLE_MFT_VOLUME_UNIQUE_ID") ||
            Path.GetFullPath(monitorTarget.GetProperty("WorkloadRoot").GetString() ?? string.Empty) != Path.GetFullPath(RequiredEnvironment("STORAGE_CHRONICLE_MFT_SEED_WORKLOAD_ROOT")))
            throw new InvalidOperationException("Independent monitor target identities do not match the benchmark seed.");
        physicalSeedPreflightValidated = true;
    }

    private static void RequireEqual(JsonElement value, string propertyName, string expected)
    {
        if (value.GetProperty(propertyName).GetString() != expected)
            throw new InvalidOperationException($"The physical MFT preflight does not match {propertyName}.");
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
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
            WindowsSeBackupPrivilegeScope? privilege = null;

            NativeFileMetadataRecord nativeMetadata;
            EventQuality quality;
            try
            {
                try
                {
                    nativeMetadata = ReadCandidateMetadataWithCurrentToken(allowAccessDeniedMetadata: false);
                    quality = EventQuality.Reconciled;
                }
                catch (Exception exception) when (IsAccessDenied(exception))
                {
                    privilege = WindowsSeBackupPrivilegeScope.Enter();
                    try
                    {
                        nativeMetadata = ReadCandidateMetadataWithCurrentToken(allowAccessDeniedMetadata: true);
                        quality = nativeMetadata.IsAccessDenied ? EventQuality.ExistenceOnly : EventQuality.Reconciled;
                    }
                    catch (Exception retryException) when (IsMetadataReadFailure(retryException))
                    {
                        nativeMetadata = FallbackMetadata(entry);
                        quality = EventQuality.ExistenceOnly;
                    }
                }
                catch (Exception exception) when (IsMetadataReadFailure(exception))
                {
                    nativeMetadata = FallbackMetadata(entry);
                    quality = EventQuality.Unknown;
                }
            }
            finally
            {
                var privilegeResult = privilege?.Result;
                privilege?.Dispose();
                priority.Dispose();
                if (privilegeResult is not null)
                {
                    if (privilegeResult.Enabled) privilegeSuccesses++;
                    else privilegeFallbacks++;
                }
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

            NativeFileMetadataRecord ReadCandidateMetadataWithCurrentToken(bool allowAccessDeniedMetadata)
            {
                using var handle = reader.OpenMetadataHandle(path, entry.IsDirectory);
                if (priority.TrySetLowFileIoPriority(handle)) ioHintSuccesses++;
                else ioHintFailures++;
                ioHintAttempts++;
                var value = reader.Read(path, Path.GetDirectoryName(path));
                if (value.IsAccessDenied && !allowAccessDeniedMetadata) throw new UnauthorizedAccessException($"Metadata access was denied: {path}");
                return value;
            }
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

    private static bool IsAccessDenied(Exception exception) => exception switch
    {
        UnauthorizedAccessException => true,
        IOException ioException => (ioException.HResult & 0xFFFF) == 5,
        System.ComponentModel.Win32Exception win32Exception => win32Exception.NativeErrorCode == 5,
        _ => false
    };

    private static bool IsMetadataReadFailure(Exception exception) => exception is UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception;

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
