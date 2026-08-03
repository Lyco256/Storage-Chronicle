using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Windows.FileSystem.Interop;
using StorageChronicle.Platform.Windows.FileSystem.Volumes;
using System.ComponentModel;
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

    [Fact]
    public async Task NotificationRegistrationFailureDoesNotStopMonitor()
    {
        await using var monitor = new WindowsExternalMediaMonitor(new ThrowingDeviceNative());

        Assert.Contains("registration unavailable", monitor.RegistrationFailure, StringComparison.Ordinal);
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task BoundedNotificationOverflowProducesAnExplicitContinuityGap()
    {
        var native = new FakeDeviceNative();
        await using var monitor = new WindowsExternalMediaMonitor(native, queueCapacity: 1);
        native.Raise(ExternalMediaChangeKind.Connected);
        native.Raise(ExternalMediaChangeKind.Disconnected);
        await monitor.DisposeAsync();

        var values = await ReadAllAsync(monitor, TestContext.Current.CancellationToken);

        Assert.Contains(values, value => value.Kind == ExternalMediaChangeKind.ContinuityGap);
        Assert.Contains(values, value => value.GapReason?.Contains("overflow", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static async Task<ExternalMediaChange> ReadOneAsync(WindowsExternalMediaMonitor monitor, CancellationToken cancellationToken)
    {
        await foreach (var change in monitor.ReadChangesAsync(cancellationToken)) return change;
        throw new InvalidOperationException("No device notification was received.");
    }

    private static async Task<IReadOnlyList<ExternalMediaChange>> ReadAllAsync(WindowsExternalMediaMonitor monitor, CancellationToken cancellationToken)
    {
        var values = new List<ExternalMediaChange>();
        await foreach (var value in monitor.ReadChangesAsync(cancellationToken)) values.Add(value);
        return values;
    }
}

internal sealed class ThrowingDeviceNative : IWindowsDeviceNotificationNative
{
    public IDisposable Register(Action<ExternalMediaChangeKind> callback) => throw new Win32Exception("registration unavailable");
}
