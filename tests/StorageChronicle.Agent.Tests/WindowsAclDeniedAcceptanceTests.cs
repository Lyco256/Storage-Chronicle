using System.Security.AccessControl;
using System.Security.Principal;
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

public sealed class WindowsAclDeniedAcceptanceTests
{
    [Fact(Skip = "Requires an elevated non-system NTFS acceptance root.", SkipUnless = nameof(IsAcceptanceEnvironmentReady))]
    [Trait("Category", "WindowsPrivileged")]
    [Trait("Capability", "AclDeniedMetadata")]
    public async Task ScopedBackupPrivilegeReadsMetadataForAnAclDeniedCandidate()
    {
        Assert.True(OperatingSystem.IsWindows(), "The ACL-denied acceptance test requires Windows.");
        var root = Required("STORAGE_CHRONICLE_ACCEPTANCE_ROOT");
        var volumes = await new WindowsVolumeEnumerator().EnumerateAsync(TestContext.Current.CancellationToken);
        var volume = Assert.Single(volumes, value => value.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) && value.MountPoints.Any(mount => IsSameOrUnder(root, mount)));
        var scenario = CreateScenario(root);
        var historyRoot = Path.Combine(Path.GetTempPath(), "StorageChronicle.AclDeniedHistory", Guid.NewGuid().ToString("N"));
        var options = new WindowsFileSystemOptions
        {
            StorageChronicleDataRoot = historyRoot,
            MonitoredRoots = new[] { scenario },
            SnapshotBatchSize = 128,
            InitialNotificationCapacity = 1024
        };
        var snapshot = new WindowsVolumeSnapshotReader(new WindowsExclusionPolicy(options), options);
        var normalizer = new EventNormalizer();
        var candidatePath = Path.Combine(scenario, "metadata-only-candidate.dat");
        FileSecurity? originalSecurity = null;

        try
        {
            using (File.Create(candidatePath)) { }
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
                var fileInfo = new FileInfo(candidatePath);
                originalSecurity = fileInfo.GetAccessControl(AccessControlSections.All);
                var identity = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The current Windows identity has no SID.");
                var deny = new FileSystemAccessRule(identity, FileSystemRights.ReadData | FileSystemRights.ReadAttributes | FileSystemRights.ReadExtendedAttributes | FileSystemRights.ReadPermissions, AccessControlType.Deny);
                var deniedSecurity = fileInfo.GetAccessControl();
                deniedSecurity.AddAccessRule(deny);
                fileInfo.SetAccessControl(deniedSecurity);

                var runner = new ConfirmedReconciliationRunner(new WindowsVolumeEnumerator(), new WindowsNtfsApi(), snapshot, new WindowsFileMetadataReader(), storage, normalizer, new AgentHealthState());
                var summary = await runner.ExecuteAsync(new PendingReconciliationRequest("acceptance-acl-denied", volume.Id, "ACL denied candidate metadata acceptance", storage.Status.LastSourceSequence, DateTimeOffset.UtcNow.AddSeconds(-1)), TestContext.Current.CancellationToken);

                Assert.True(summary.Completed, summary.FailureReason ?? summary.Status);
                Assert.True(summary.DurableEventCount > 0, "The production runner appended no ACL-denied candidate event.");
                Assert.True(summary.PrivilegeEnableSuccessCount > 0, "The production runner did not enable SeBackupPrivilege in its bounded scope.");
                var events = await ReadCanonicalAsync(storage, TestContext.Current.CancellationToken);
                Assert.Contains(events, value => string.Equals(value.Name, Path.GetFileName(candidatePath), StringComparison.OrdinalIgnoreCase) && value.Operation == CanonicalOperation.ReconciliationDiscovered && value.Metadata is not null && value.Metadata.Quality == EventQuality.Reconciled);
            }
        }
        finally
        {
            if (originalSecurity is not null && File.Exists(candidatePath))
            {
                try { new FileInfo(candidatePath).SetAccessControl(originalSecurity); } catch (UnauthorizedAccessException) { }
            }

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
        var scenario = Path.Combine(root, $"StorageChronicle.Acceptance.acl-denied.{Guid.NewGuid():N}");
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
        await foreach (var value in storage.ReadCanonicalAsync(cancellationToken)) values.Add(value);
        return values;
    }
}
