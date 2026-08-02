using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Windows.FileSystem;
using StorageChronicle.Platform.Windows.FileSystem.Policy;
using StorageChronicle.Platform.Windows.FileSystem.Reconciliation;
using Xunit;

namespace StorageChronicle.Platform.Windows.FileSystem.Tests;

public sealed class PolicyAndReconciliationTests
{
    [Fact]
    public void StorageStandardAndUserExclusionsApplyBeforeEventGeneration()
    {
        var policy = new WindowsExclusionPolicy(new WindowsFileSystemOptions { StorageChronicleDataRoot = "C:\\Chronicle", UserExcludedRoots = ["C:\\private"] });

        Assert.True(policy.ShouldExclude("C:\\Chronicle\\events.db"));
        Assert.True(policy.ShouldExclude("C:\\private\\secret.txt"));
        Assert.True(policy.ShouldExclude("C:\\System Volume Information"));
        Assert.False(policy.ShouldExclude("C:\\Chronicle-old\\events.db"));
    }

    [Fact]
    public void SubdirectoryMonitoringDoesNotPretendTheWholeNtfsVolumeIsScoped()
    {
        var policy = new WindowsExclusionPolicy(new WindowsFileSystemOptions { MonitoredRoots = ["C:\\Users\\alice\\Documents"] });

        Assert.True(policy.IsMonitoredRoot("C:\\"));
        Assert.False(policy.IsWholeVolumeMonitored("C:\\"));
    }

    [Fact]
    public async Task DecliningReconciliationLeavesGapAndDoesNotReturnDiff()
    {
        var reconciler = new DirectoryReconciler();
        var confirmationCalled = false;
        var result = await reconciler.ReconcileAsync(
            VolumeId.Create("V"),
            [new DirectoryReconciliationEntry(FileId.Create("1"), "old.txt", FileKind.File)],
            [new DirectoryReconciliationEntry(FileId.Create("1"), "new.txt", FileKind.File)],
            _ => { confirmationCalled = true; return ValueTask.FromResult(false); },
            TestContext.Current.CancellationToken);

        Assert.True(confirmationCalled);
        Assert.False(result.Started);
        Assert.Empty(result.Changes);
        Assert.True(result.Gap!.UserDeclined);
    }

    [Fact]
    public async Task ConfirmedReconciliationReturnsOnlyPathDiffWithReconciledQuality()
    {
        var reconciler = new DirectoryReconciler();
        var result = await reconciler.ReconcileAsync(
            VolumeId.Create("V"),
            [new DirectoryReconciliationEntry(FileId.Create("1"), "old.txt", FileKind.File)],
            [new DirectoryReconciliationEntry(FileId.Create("1"), "new.txt", FileKind.File), new DirectoryReconciliationEntry(FileId.Create("2"), "added.txt", FileKind.File)],
            _ => ValueTask.FromResult(true),
            TestContext.Current.CancellationToken);

        Assert.True(result.Started);
        Assert.Null(result.Gap);
        Assert.Equal(2, result.Changes.Count);
        Assert.All(result.Changes, change => Assert.Equal(EventQuality.Reconciled, change.Quality));
    }
}
