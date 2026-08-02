using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Windows.FileSystem.Interop;
using StorageChronicle.Platform.Windows.FileSystem.Volumes;
using Xunit;

namespace StorageChronicle.Platform.Windows.FileSystem.Tests;

public sealed class VolumeAndMediaTests
{
    [Fact]
    public async Task VolumeWithoutDriveLetterIsRetained()
    {
        var native = new FakeVolumeNative([new NativeVolumeRecord("\\\\?\\Volume{ABC}\\", "exFAT", Array.Empty<string>(), StorageChronicle.Platform.Windows.FileSystem.Interop.DriveType.Fixed, false, true, false)]);
        var enumerator = new WindowsVolumeEnumerator(native);

        var volumes = await enumerator.EnumerateAsync(TestContext.Current.CancellationToken);

        var volume = Assert.Single(volumes);
        Assert.Equal("\\\\?\\VOLUME{ABC}", volume.Id.Value);
        Assert.Empty(volume.MountPoints);
        Assert.False(volume.SupportsUsn);
        Assert.True(volume.IsDirectoryReadable);
    }

    [Fact]
    public void RefsJournalContinuityIsConservativelyUnsupported()
    {
        var capabilities = new WindowsPlatformCapabilities();

        Assert.True(capabilities.IsSupported("ReadDirectoryChangesW"));
        Assert.False(capabilities.IsSupported("ReFSJournalContinuity"));
        Assert.False(capabilities.IsSupported("unknown"));
    }

    [Fact]
    public async Task DeviceNotificationIsEventDrivenAndDoesNotPoll()
    {
        var native = new FakeDeviceNative();
        await using var monitor = new WindowsExternalMediaMonitor(native);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var task = ReadOneAsync(monitor, cancellation.Token);
        native.Raise(ExternalMediaChangeKind.Connected);

        var change = await task;

        Assert.Equal(ExternalMediaChangeKind.Connected, change.Kind);
    }

    private static async Task<ExternalMediaChange> ReadOneAsync(WindowsExternalMediaMonitor monitor, CancellationToken cancellationToken)
    {
        await foreach (var change in monitor.ReadChangesAsync(cancellationToken)) return change;
        throw new InvalidOperationException("No device notification was received.");
    }
}
