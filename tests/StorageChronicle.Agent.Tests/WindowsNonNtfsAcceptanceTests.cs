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

public sealed class WindowsNonNtfsAcceptanceTests
{
    [Fact(Skip = "Requires an elevated non-system non-NTFS acceptance root.", SkipUnless = nameof(IsAcceptanceEnvironmentReady))]
    [Trait("Category", "WindowsPrivileged")]
    [Trait("Capability", "NonNtfs")]
    public async Task NonNtfsSnapshotReconciliationRecordsARealDirectoryDifference()
    {
        Assert.True(OperatingSystem.IsWindows(), "The non-NTFS acceptance test requires Windows.");
        var root = Required("STORAGE_CHRONICLE_ACCEPTANCE_ROOT");
        var volumeEnumerator = new WindowsVolumeEnumerator();
        var volume = Assert.Single(await volumeEnumerator.EnumerateAsync(TestContext.Current.CancellationToken), value => value.MountPoints.Any(mount => IsSameOrUnder(root, mount)));
        Assert.False(string.Equals("NTFS", volume.FileSystem, StringComparison.OrdinalIgnoreCase));
        var scenario = CreateScenario(root);
        var historyRoot = Path.Combine(Path.GetTempPath(), "StorageChronicle.NonNtfsAcceptanceHistory", Guid.NewGuid().ToString("N"));
        var options = new WindowsFileSystemOptions
        {
            StorageChronicleDataRoot = historyRoot,
            MonitoredRoots = new[] { scenario },
            SnapshotBatchSize = 128,
            InitialNotificationCapacity = 1024
        };
        var snapshot = new WindowsVolumeSnapshotReader(new WindowsExclusionPolicy(options), options);
        var normalizer = new EventNormalizer();
        var seedPath = Path.Combine(scenario, "non-ntfs-seed-entry.dat");
        var changedPath = Path.Combine(scenario, "non-ntfs-changed-entry.dat");

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
                }

                await storage.FlushAsync(TestContext.Current.CancellationToken);
                using (File.Create(changedPath)) { }
                var runner = new ConfirmedReconciliationRunner(volumeEnumerator, new WindowsNtfsApi(), snapshot, new WindowsFileMetadataReader(), storage, normalizer, new AgentHealthState());
                var summary = await runner.ExecuteAsync(new PendingReconciliationRequest("acceptance-non-ntfs", volume.Id, "non-NTFS directory reconciliation acceptance", storage.Status.LastSourceSequence, DateTimeOffset.UtcNow.AddSeconds(-1)), TestContext.Current.CancellationToken);

                Assert.True(summary.Completed, summary.FailureReason ?? summary.Status);
                Assert.True(summary.DurableEventCount > 0, "The non-NTFS production runner appended no real difference.");
                Assert.Equal(volume.FileSystem, summary.FileSystem);
                var events = new List<CanonicalEvent>();
                await foreach (var value in storage.ReadCanonicalAsync(TestContext.Current.CancellationToken)) events.Add(value);
                Assert.Contains(events, value => value.Origin == EventOrigin.DirectoryReconciliation && string.Equals(value.Name, Path.GetFileName(changedPath), StringComparison.OrdinalIgnoreCase) && value.ProcessQuality == ProcessAttributionQuality.Unknown);
            }
        }
        finally
        {
            DeleteScenario(scenario);
            if (Directory.Exists(historyRoot)) Directory.Delete(historyRoot, recursive: true);
        }
    }

    public static bool IsAcceptanceEnvironmentReady => OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STORAGE_CHRONICLE_ACCEPTANCE_ROOT"));

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? Path.GetFullPath(value) : throw new InvalidOperationException($"{name} is required for a privileged acceptance run.");

    private static string CreateScenario(string root)
    {
        var volumeRoot = Path.GetPathRoot(root);
        if (string.IsNullOrWhiteSpace(volumeRoot) || string.Equals(volumeRoot, "C:\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The acceptance test refuses the guest system volume.");
        var scenario = Path.Combine(root, $"StorageChronicle.Acceptance.non-ntfs.{Guid.NewGuid():N}");
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
}
