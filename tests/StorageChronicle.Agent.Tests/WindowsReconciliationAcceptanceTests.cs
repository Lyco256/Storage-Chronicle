using System.Text.Json;
using StorageChronicle.Agent;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Normalization;
using StorageChronicle.Platform.Windows.FileSystem;
using StorageChronicle.Platform.Windows.FileSystem.Policy;
using StorageChronicle.Platform.Windows.FileSystem.Snapshot;
using StorageChronicle.Platform.Windows.FileSystem.Volumes;
using StorageChronicle.Platform.Windows.Ntfs;
using StorageChronicle.Storage;
using Xunit;

namespace StorageChronicle.Agent.Tests;

public sealed class WindowsReconciliationAcceptanceTests
{
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { WriteIndented = true };

    [Fact(Skip = "Requires an elevated non-system NTFS acceptance root.", SkipUnless = nameof(IsAcceptanceEnvironmentReady))]
    [Trait("Category", "WindowsPrivileged")]
    [Trait("Capability", "Reconciliation")]
    public async Task ConfirmedReconciliationAppendsARealDifferenceThroughTheProductionRunner()
    {
        Assert.True(OperatingSystem.IsWindows(), "The Windows reconciliation acceptance test requires Windows.");
        var root = Required("STORAGE_CHRONICLE_ACCEPTANCE_ROOT");
        var evidencePath = Environment.GetEnvironmentVariable("STORAGE_CHRONICLE_RECONCILIATION_EVIDENCE_PATH");
        var volumeEnumerator = new WindowsVolumeEnumerator();
        var volumes = await volumeEnumerator.EnumerateAsync(TestContext.Current.CancellationToken);
        var volume = Assert.Single(volumes, value => value.MountPoints.Any(mount => IsSameOrUnder(root, mount)));
        var scenario = CreateScenario(root, "reconciliation");
        var historyRoot = Path.Combine(Path.GetTempPath(), "StorageChronicle.AcceptanceHistory", Guid.NewGuid().ToString("N"));
        var options = new WindowsFileSystemOptions
        {
            StorageChronicleDataRoot = historyRoot,
            MonitoredRoots = new[] { scenario },
            SnapshotBatchSize = 128,
            InitialNotificationCapacity = 1024
        };
        var snapshot = new WindowsVolumeSnapshotReader(new WindowsExclusionPolicy(options), options);
        var normalizer = new EventNormalizer();
        var seedPath = Path.Combine(scenario, "seed-entry.dat");
        var changedPath = Path.Combine(scenario, "changed-entry.dat");
        var sourceEventCount = 0;
        ReconciliationExecutionSummary? summary = null;

        try
        {
            using (File.Create(seedPath)) { }
            await using (var storage = new AppendOnlyStorageEngine(new StorageEngineOptions(historyRoot) { FlushInterval = TimeSpan.FromMinutes(1) }))
            {
                await foreach (var source in snapshot.ReadInitialSnapshotAsync(volume, TestContext.Current.CancellationToken))
                {
                    var canonical = normalizer.Normalize(source);
                    if (canonical is null) continue;
                    await storage.AppendSourceAsync(source, TestContext.Current.CancellationToken);
                    await storage.AppendCanonicalAsync(canonical, TestContext.Current.CancellationToken);
                    await storage.ApplyAsync(canonical, TestContext.Current.CancellationToken);
                    sourceEventCount++;
                }

                await storage.FlushAsync(TestContext.Current.CancellationToken);
                using (File.Create(changedPath)) { }
                var runner = new ConfirmedReconciliationRunner(volumeEnumerator, new WindowsNtfsApi(), snapshot, new WindowsFileMetadataReader(), storage, normalizer, new AgentHealthState());
                summary = await runner.ExecuteAsync(new PendingReconciliationRequest("acceptance-reconciliation", volume.Id, "real acceptance gap", storage.Status.LastSourceSequence, DateTimeOffset.UtcNow.AddSeconds(-1)), TestContext.Current.CancellationToken);

                Assert.True(summary.Completed, summary.FailureReason ?? summary.Status);
                Assert.True(summary.DurableEventCount > 0, "The production reconciliation runner appended no real differences.");
                var canonicalEvents = await ReadCanonicalAsync(storage, TestContext.Current.CancellationToken);
                Assert.Contains(canonicalEvents, value => value.Operation == CanonicalOperation.ReconciliationDiscovered &&
                    string.Equals(value.Name, Path.GetFileName(changedPath), StringComparison.OrdinalIgnoreCase) &&
                    (value.Origin is EventOrigin.MftReconciliation or EventOrigin.DirectoryReconciliation) &&
                    value.ProcessQuality == ProcessAttributionQuality.Unknown);
                var finalState = await storage.GetSnapshotAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
                WriteEvidence(evidencePath, new
                {
                    Schema = "StorageChronicle.ConfirmedReconciliationAcceptance.v1",
                    AcceptanceEligible = true,
                    Status = "PASSED",
                    RunId = summary.RunId,
                    VolumeId = summary.VolumeId.Value,
                    FileSystem = summary.FileSystem,
                    SourceEventCount = sourceEventCount,
                    CanonicalEventCount = canonicalEvents.Count,
                    FinalStateCount = finalState.Entries.Count,
                    DurableEventCount = summary.DurableEventCount,
                    LightweightEntryCount = summary.LightweightEntryCount,
                    CandidateCount = summary.CandidateCount,
                    DetailedMetadataQueryCount = summary.DetailedMetadataQueryCount,
                    DetailedQueryCandidateRatio = summary.DetailedQueryCandidateRatio,
                    PrivilegeEnableSuccessCount = summary.PrivilegeEnableSuccessCount,
                    PrivilegeEnableFailureCount = summary.PrivilegeEnableFailureCount,
                    PrivilegeFallbackCount = summary.PrivilegeFallbackCount,
                    AclFallbackCount = summary.AclFallbackCount,
                    BackgroundModeEnabled = summary.Priority.BackgroundModeEnabled,
                    BackgroundStartError = summary.Priority.BackgroundStartError,
                    BackgroundEndError = summary.Priority.BackgroundEndError,
                    IoHintAttempts = summary.Priority.IoHintAttempts,
                    IoHintSuccesses = summary.Priority.IoHintSuccesses,
                    IoHintFailures = summary.Priority.IoHintFailures,
                    StartedUtc = summary.StartedUtc,
                    FinishedUtc = summary.FinishedUtc,
                    ElapsedMilliseconds = summary.ElapsedMilliseconds,
                    FailureReason = summary.FailureReason
                });
            }
        }
        catch (Exception exception)
        {
            WriteEvidence(evidencePath, new
            {
                Schema = "StorageChronicle.ConfirmedReconciliationAcceptance.v1",
                AcceptanceEligible = false,
                Status = "FAILED",
                RunId = summary?.RunId,
                VolumeId = volume.Id.Value,
                FileSystem = volume.FileSystem,
                SourceEventCount = sourceEventCount,
                CanonicalEventCount = 0,
                FinalStateCount = 0,
                DurableEventCount = summary?.DurableEventCount ?? 0,
                LightweightEntryCount = summary?.LightweightEntryCount ?? 0,
                CandidateCount = summary?.CandidateCount ?? 0,
                DetailedMetadataQueryCount = summary?.DetailedMetadataQueryCount ?? 0,
                DetailedQueryCandidateRatio = summary?.DetailedQueryCandidateRatio ?? 0d,
                PrivilegeEnableSuccessCount = summary?.PrivilegeEnableSuccessCount ?? 0,
                PrivilegeEnableFailureCount = summary?.PrivilegeEnableFailureCount ?? 0,
                PrivilegeFallbackCount = summary?.PrivilegeFallbackCount ?? 0,
                AclFallbackCount = summary?.AclFallbackCount ?? 0,
                BackgroundModeEnabled = summary?.Priority.BackgroundModeEnabled ?? false,
                BackgroundStartError = summary?.Priority.BackgroundStartError,
                BackgroundEndError = summary?.Priority.BackgroundEndError,
                IoHintAttempts = summary?.Priority.IoHintAttempts ?? 0,
                IoHintSuccesses = summary?.Priority.IoHintSuccesses ?? 0,
                IoHintFailures = summary?.Priority.IoHintFailures ?? 0,
                StartedUtc = summary?.StartedUtc,
                FinishedUtc = summary?.FinishedUtc,
                ElapsedMilliseconds = summary?.ElapsedMilliseconds ?? 0d,
                FailureReason = exception.ToString()
            });
            throw;
        }
        finally
        {
            DeleteScenario(scenario);
            if (Directory.Exists(historyRoot)) Directory.Delete(historyRoot, recursive: true);
        }
    }

    public static bool IsAcceptanceEnvironmentReady => OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STORAGE_CHRONICLE_ACCEPTANCE_ROOT"));

    private static string Required(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{name} is required for a privileged acceptance run.");
        return Path.GetFullPath(value);
    }

    private static void WriteEvidence(string? path, object value)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("The reconciliation evidence path has no parent directory.");
        Directory.CreateDirectory(parent);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(value, EvidenceJsonOptions));
    }

    private static string CreateScenario(string root, string name)
    {
        var volumeRoot = Path.GetPathRoot(root);
        if (string.IsNullOrWhiteSpace(volumeRoot) || string.Equals(volumeRoot, "C:\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The acceptance test refuses the guest system volume.");
        var scenario = Path.Combine(root, $"StorageChronicle.Acceptance.{name}.{Guid.NewGuid():N}");
        Directory.CreateDirectory(scenario);
        return scenario;
    }

    private static void DeleteScenario(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static bool IsSameOrUnder(string candidate, string root)
    {
        var candidatePath = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(candidatePath, rootPath, StringComparison.OrdinalIgnoreCase) || candidatePath.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<CanonicalEvent>> ReadCanonicalAsync(AppendOnlyStorageEngine storage, CancellationToken cancellationToken)
    {
        var values = new List<CanonicalEvent>();
        var offset = 0;
        while (true)
        {
            var page = await storage.ReadCanonicalPageAsync(offset, 512, cancellationToken: cancellationToken);
            if (page.Count == 0) break;
            values.AddRange(page);
            offset += page.Count;
            if (page.Count < 512) break;
        }

        return values;
    }
}
