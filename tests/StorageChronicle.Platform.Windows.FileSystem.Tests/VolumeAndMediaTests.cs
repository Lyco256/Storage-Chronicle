using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Windows.FileSystem.Interop;
using StorageChronicle.Platform.Windows.FileSystem.Volumes;
using System.Text.Json;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using StorageChronicle.Contracts;
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
        Assert.False(volume.IsProtectedRoleClassificationComplete);
    }

    [Fact]
    public void ProtectedRolesAreReadFromOneUnambiguousPartitionAndFailClosedOnUnknownLayouts()
    {
        const string volumePath = @"\\?\Volume{AABBCCDD-0000-0000-0000-000000000001}\";
        var efi = WindowsVolumeRoleClassifier.Classify(volumePath, [], [new NativePartitionRoleRecord([volumePath], false, false, "C12A7328-F81F-11D2-BA4B-00A0C93EC93B", null)], true);
        var recovery = WindowsVolumeRoleClassifier.Classify(volumePath, [], [new NativePartitionRoleRecord([volumePath], false, false, "DE94BBA4-06D1-4D40-A16A-BFD50179D6AC", null)], true);
        var boot = WindowsVolumeRoleClassifier.Classify(volumePath, [], [new NativePartitionRoleRecord([volumePath], true, true, "EBD0A0A2-B9E5-4433-87C0-68B6B72699C7", null)], true);
        var unknownType = WindowsVolumeRoleClassifier.Classify(volumePath, [], [new NativePartitionRoleRecord([volumePath], false, false, "00000000-0000-0000-0000-000000000000", null)], true);
        var noMatch = WindowsVolumeRoleClassifier.Classify(volumePath, [], [new NativePartitionRoleRecord([@"\\?\Volume{OTHER}\"], false, false, "EBD0A0A2-B9E5-4433-87C0-68B6B72699C7", null)], true);
        var ambiguous = WindowsVolumeRoleClassifier.Classify(volumePath, [], [new NativePartitionRoleRecord([volumePath], false, false, null, 0x07), new NativePartitionRoleRecord([volumePath], false, false, null, 0x07)], true);
        var failedQuery = WindowsVolumeRoleClassifier.Classify(volumePath, [], [], false);

        Assert.True(efi.IsComplete);
        Assert.Equal(ProtectedVolumeRoles.Efi, efi.Roles);
        Assert.Equal(ProtectedVolumeRoles.Recovery, recovery.Roles);
        Assert.Equal(ProtectedVolumeRoles.System | ProtectedVolumeRoles.Boot, boot.Roles);
        Assert.False(unknownType.IsComplete);
        Assert.True(unknownType.Roles.HasFlag(ProtectedVolumeRoles.Unknown));
        Assert.False(noMatch.IsComplete);
        Assert.False(ambiguous.IsComplete);
        Assert.False(failedQuery.IsComplete);
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
    public void VolumeDirectorySessionCreatesAndRenamesOnlyRelativeToPinnedVolumeHandles()
    {
        var fixture = CreateOwnedFixture("storage-chronicle-volume-session-", out var runId);
        try
        {
            var mountPoint = new char[1024];
            Assert.True(GetVolumePathName(fixture.FullName, mountPoint, (uint)mountPoint.Length));
            var volumeGuid = new char[1024];
            Assert.True(GetVolumeNameForVolumeMountPoint(new string(mountPoint).TrimEnd('\0'), volumeGuid, (uint)volumeGuid.Length));

            using var session = WindowsVolumeDirectorySession.OpenAtExistingDirectory(new string(volumeGuid).TrimEnd('\0'), new string(mountPoint).TrimEnd('\0'), fixture.FullName);
            using var fixtureHandle = session.DuplicateRootHandle();
            var childName = "storage-chronicle-fixture-" + Guid.NewGuid().ToString("N");
            using var chronicleDirectory = session.OpenOrCreateDirectory(fixtureHandle, childName);
            using var stream = session.CreateNewFile(chronicleDirectory, "pending.tmp");
            stream.Write("fixture-bytes"u8);
            stream.Flush(flushToDisk: true);
            session.MoveFile(stream.SafeFileHandle, chronicleDirectory, "final.seg");
            stream.Dispose();

            var finalPath = Path.Combine(fixture.FullName, childName, "final.seg");
            Assert.Equal("fixture-bytes", File.ReadAllText(finalPath));
            Assert.False(File.Exists(Path.Combine(fixture.FullName, childName, "pending.tmp")));
            using var competing = session.CreateNewFile(chronicleDirectory, "competing.tmp");
            competing.Write("preserve-me"u8);
            competing.Flush(flushToDisk: true);
            Assert.Throws<Win32Exception>(() => session.MoveFile(competing.SafeFileHandle, chronicleDirectory, "final.seg"));
            competing.Dispose();
            Assert.Equal("fixture-bytes", File.ReadAllText(finalPath));
            Assert.Throws<ArgumentException>(() => session.CreateNewFile(chronicleDirectory, ".."));
        }
        finally
        {
            DeleteOwnedFixture(fixture.FullName, "storage-chronicle-volume-session-", runId);
        }
    }

    [Fact]
    public void VolumeDirectorySessionRejectsAReparseAncestorInAnExistingFixturePathWhenSupported()
    {
        var fixture = CreateOwnedFixture("storage-chronicle-reparse-ancestor-", out var runId);
        var linkPath = Path.Combine(fixture.FullName, "redirect");
        var linkCreated = false;
        try
        {
            var target = Directory.CreateDirectory(Path.Combine(fixture.FullName, "real-target"));
            var nested = Directory.CreateDirectory(Path.Combine(target.FullName, "nested"));
            try
            {
                Directory.CreateSymbolicLink(linkPath, target.FullName);
                linkCreated = true;
            }
            catch (UnauthorizedAccessException) { return; }
            catch (PlatformNotSupportedException) { return; }

            var mountPoint = new char[1024];
            Assert.True(GetVolumePathName(nested.FullName, mountPoint, (uint)mountPoint.Length));
            var volumeGuid = new char[1024];
            var mount = new string(mountPoint).TrimEnd('\0');
            Assert.True(GetVolumeNameForVolumeMountPoint(mount, volumeGuid, (uint)volumeGuid.Length));

            Assert.Throws<IOException>(() => WindowsVolumeDirectorySession.OpenAtExistingDirectory(
                new string(volumeGuid).TrimEnd('\0'), mount, Path.Combine(linkPath, "nested")));
        }
        finally
        {
            if (linkCreated) Directory.Delete(linkPath);
            DeleteOwnedFixture(fixture.FullName, "storage-chronicle-reparse-ancestor-", runId);
        }
    }

    [Fact]
    public void PublicVolumeSessionIsConfinedAndOnlyMovesItsOwnCreatedFileHandle()
    {
        var fixture = CreateOwnedFixture("storage-chronicle-media-boundary-", out var runId);
        try
        {
            var mountPoint = new char[1024];
            Assert.True(GetVolumePathName(fixture.FullName, mountPoint, (uint)mountPoint.Length));
            var volumeGuid = new char[1024];
            var mount = new string(mountPoint).TrimEnd('\0');
            Assert.True(GetVolumeNameForVolumeMountPoint(mount, volumeGuid, (uint)volumeGuid.Length));
            using var session = WindowsVolumeDirectorySession.OpenAtExistingDirectory(new string(volumeGuid).TrimEnd('\0'), mount, fixture.FullName);

            Assert.True(session.TryCreateDirectory(".StorageChronicle"));
            using (var marker = session.CreateNew(".StorageChronicle\\.storage-chronicle-owner.json"))
            {
                marker.Write("{\"schema\":\"StorageChronicle.MediaOwnership.v1\",\"writerId\":null}"u8);
                marker.Flush();
            }
            session.EnsureDirectory(".StorageChronicle\\writers\\pc-test");
            var generation = Guid.NewGuid().ToString("N");
            var temporary = $".StorageChronicle\\writers\\pc-test\\{generation}.tmp";
            var final = $".StorageChronicle\\writers\\pc-test\\{generation}.seg";
            using (var created = session.CreateNew(temporary))
            {
                created.Write("verified-fixture"u8);
                created.Flush();
                session.MoveCreatedFile(created, final);
                Assert.Throws<ArgumentException>(() => session.MoveCreatedFile(created, ".StorageChronicle\\writers\\pc-test\\other.seg"));
                Assert.Throws<ObjectDisposedException>(() => created.WriteByte(0x42));
            }

            Assert.Equal("verified-fixture", File.ReadAllText(Path.Combine(fixture.FullName, ".StorageChronicle", "writers", "pc-test", generation + ".seg")));
            Assert.Contains(session.EnumerateEntries(".StorageChronicle\\writers\\pc-test"), entry => entry.Name == generation + ".seg");
            Assert.Throws<ArgumentException>(() => session.CreateNew("Users\\unrelated.txt"));

            using var externalStream = new FileStream(Path.Combine(fixture.FullName, "external.txt"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Delete);
            Assert.Throws<ArgumentException>(() => session.MoveCreatedFile(externalStream, final));
            Assert.True(File.Exists(Path.Combine(fixture.FullName, "external.txt")));
        }
        finally
        {
            DeleteOwnedFixture(fixture.FullName, "storage-chronicle-media-boundary-", runId);
        }
    }

    [Fact]
    public void PublicVolumeSessionDoesNotAdoptAnExistingUnmarkedProductDirectory()
    {
        var fixture = CreateOwnedFixture("storage-chronicle-unmarked-media-", out var runId);
        try
        {
            Directory.CreateDirectory(Path.Combine(fixture.FullName, ".StorageChronicle"));
            var mountPoint = new char[1024];
            Assert.True(GetVolumePathName(fixture.FullName, mountPoint, (uint)mountPoint.Length));
            var volumeGuid = new char[1024];
            var mount = new string(mountPoint).TrimEnd('\0');
            Assert.True(GetVolumeNameForVolumeMountPoint(mount, volumeGuid, (uint)volumeGuid.Length));
            using var session = WindowsVolumeDirectorySession.OpenAtExistingDirectory(new string(volumeGuid).TrimEnd('\0'), mount, fixture.FullName);

            Assert.False(session.TryCreateDirectory(".StorageChronicle"));
            Assert.Throws<IOException>(() => session.CreateNew(".StorageChronicle\\.storage-chronicle-owner.json"));
            Assert.False(File.Exists(Path.Combine(fixture.FullName, ".StorageChronicle", ".storage-chronicle-owner.json")));
        }
        finally
        {
            DeleteOwnedFixture(fixture.FullName, "storage-chronicle-unmarked-media-", runId);
        }
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

    private static DirectoryInfo CreateOwnedFixture(string prefix, out string runId)
    {
        var fixture = Directory.CreateTempSubdirectory(prefix);
        runId = Guid.NewGuid().ToString("N");
        var markerPath = Path.Combine(fixture.FullName, ".test-owner.json");
        using var marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(marker, new TestFixtureOwner("StorageChronicle.TestFixtureOwner.v1", runId));
        marker.Flush(flushToDisk: true);
        return fixture;
    }

    private static void DeleteOwnedFixture(string path, string prefix, string runId)
    {
        var fullPath = Path.GetFullPath(path);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var relative = Path.GetRelativePath(tempRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            !Path.GetFileName(fullPath).StartsWith(prefix, StringComparison.Ordinal) || !Directory.Exists(fullPath))
        {
            throw new InvalidOperationException("Refusing to remove a fixture outside its owned temporary root.");
        }

        var markerPath = Path.Combine(fullPath, ".test-owner.json");
        TestFixtureOwner? owner;
        using (var marker = File.OpenRead(markerPath))
        {
            owner = JsonSerializer.Deserialize<TestFixtureOwner>(marker);
        }
        if (owner is null || owner.Schema != "StorageChronicle.TestFixtureOwner.v1" || owner.RunId != runId)
        {
            throw new InvalidOperationException("Refusing to remove a fixture without its matching run marker.");
        }

        Directory.Delete(fullPath, recursive: true);
    }

    private sealed record TestFixtureOwner(string Schema, string RunId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumePathNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string fileName, [Out] char[] volumePathName, uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeNameForVolumeMountPointW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, [Out] char[] volumeName, uint bufferLength);
}

internal sealed class ThrowingDeviceNative : IWindowsDeviceNotificationNative
{
    public IDisposable Register(Action<ExternalMediaChangeKind> callback) => throw new Win32Exception("registration unavailable");
}
