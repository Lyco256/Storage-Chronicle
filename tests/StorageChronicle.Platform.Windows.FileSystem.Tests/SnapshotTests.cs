using System.Collections.Immutable;
using Microsoft.Win32.SafeHandles;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Windows.FileSystem;
using StorageChronicle.Platform.Windows.FileSystem.Interop;
using StorageChronicle.Platform.Windows.FileSystem.Policy;
using StorageChronicle.Platform.Windows.FileSystem.Snapshot;
using Xunit;

namespace StorageChronicle.Platform.Windows.FileSystem.Tests;

public sealed class SnapshotTests
{
    [Fact]
    public async Task SnapshotUsesTheOpenedDirectoryObjectAfterItsPathIsReplaced()
    {
        var root = Directory.CreateTempSubdirectory("storage-chronicle-snapshot-handle");
        try
        {
            var native = new ReplacedPathFileNative(root.FullName);
            var volume = new VolumeDescriptor(VolumeId.Create("V"), "FAT32", [root.FullName], false, true, false, false, true);
            var reader = new WindowsVolumeSnapshotReader(native);

            var events = await ReadAllAsync(reader.ReadInitialSnapshotAsync(volume, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

            Assert.Contains(events, value => value.Name == "candidate" && value.Metadata?.Kind == FileKind.Directory);
            Assert.Contains(events, value => value.Hint == CanonicalOperation.UnverifiedGap);
            Assert.DoesNotContain(events, value => value.Name == "target-child");
            Assert.Equal(2, native.OpenedPaths.Count);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task SnapshotUsesVolumeGuidRootInsteadOfAReparseMountPointAlias()
    {
        var mount = Directory.CreateTempSubdirectory("storage-chronicle-volume-mount");
        try
        {
            var native = new FakeFileNative();
            var volume = new VolumeDescriptor(VolumeId.Create(@"\\?\Volume{ABC}"), "NTFS", [mount.FullName], false, false, false, true, true);
            var reader = new WindowsVolumeSnapshotReader(native);

            _ = await ReadAllAsync(reader.ReadInitialSnapshotAsync(volume, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

            Assert.Equal(@"\\?\Volume{ABC}\", Assert.Single(native.OpenedPaths));
        }
        finally
        {
            mount.Delete(true);
        }
    }

    [Fact]
    [Trait("Category", "WindowsApi")]
    public async Task NativeSnapshotRecordsFinalSymbolicLinkWithoutEnumeratingItsTarget()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("This test requires Windows file-handle metadata and directory enumeration.");
        var sandbox = Directory.CreateTempSubdirectory("storage-chronicle-native-reparse");
        var root = Directory.CreateDirectory(Path.Combine(sandbox.FullName, "root"));
        var target = Directory.CreateDirectory(Path.Combine(sandbox.FullName, "target"));
        var link = Path.Combine(root.FullName, "link");
        File.WriteAllText(Path.Combine(target.FullName, "hidden-target-child.txt"), "test data not read by collector");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, target.FullName);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                Assert.Skip($"The host cannot create an unprivileged directory symbolic link: {exception.GetType().Name}.");
            }

            var nativeType = typeof(WindowsVolumeSnapshotReader).Assembly.GetType("StorageChronicle.Platform.Windows.FileSystem.Interop.WindowsNativeApi", throwOnError: true)!;
            var native = Assert.IsAssignableFrom<IWindowsFileMetadataNative>(Activator.CreateInstance(nativeType, nonPublic: true));
            var volume = new VolumeDescriptor(VolumeId.Create("V"), "NTFS", [root.FullName], false, false, false, true, true);
            var reader = new WindowsVolumeSnapshotReader(native);
            var events = await ReadAllAsync(reader.ReadInitialSnapshotAsync(volume, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

            Assert.Contains(events, value => value.Name == "link" && value.Metadata?.Kind == FileKind.SymbolicLink && (value.Metadata.Attributes & FileAttributes.ReparsePoint) != 0);
            Assert.DoesNotContain(events, value => value.Name == "hidden-target-child.txt");
            Assert.DoesNotContain(events, value => value.Hint == CanonicalOperation.UnverifiedGap);
        }
        finally
        {
            sandbox.Delete(true);
        }
    }

    [Fact]
    public async Task InitialSnapshotRecordsReparsePointButDoesNotTraverseIt()
    {
        var root = Directory.CreateTempSubdirectory("storage-chronicle-snapshot");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "junction-loop", "hidden"));
            File.WriteAllText(Path.Combine(root.FullName, "visible.txt"), "not read by collector");
            var native = new FakeFileNative(path =>
            {
                var name = Path.GetFileName(path);
                if (name == "junction-loop") return new NativeFileMetadataRecord(FileId.Create("junction"), FileId.Create("root"), name, FileKind.Junction, null, null, null, null, null, null, FileAttributes.Directory | FileAttributes.ReparsePoint, "Junction", true, false);
                return new NativeFileMetadataRecord(FileId.Create(name == root.Name ? "root" : name), FileId.Create("root"), name, name == root.Name ? FileKind.Directory : FileKind.File, 1, null, null, null, null, null, name == root.Name ? FileAttributes.Directory : FileAttributes.Normal, null, true, false);
            });
            var volume = new VolumeDescriptor(VolumeId.Create("V"), "FAT32", [root.FullName], false, true, false, false, true);
            var reader = new WindowsVolumeSnapshotReader(native, new WindowsExclusionPolicy(), new WindowsFileSystemOptions { SnapshotBatchSize = 1 });

            var events = await ReadAllAsync(reader.ReadInitialSnapshotAsync(volume, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

            Assert.Contains(events, value => value.Metadata?.Kind == FileKind.Junction);
            Assert.DoesNotContain(events, value => value.Name == "hidden");
            Assert.All(events, value => Assert.DoesNotContain(value.Properties.Keys, key => key.Contains("content", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task AccessDeniedMetadataFallsBackToExistenceOnly()
    {
        var root = Directory.CreateTempSubdirectory("storage-chronicle-access");
        try
        {
            var denied = Path.Combine(root.FullName, "denied.txt");
            File.WriteAllText(denied, "placeholder");
            var native = new FakeFileNative(path => new NativeFileMetadataRecord(FileId.Create("id:" + path), null, Path.GetFileName(path), FileKind.File, null, null, null, null, null, null, FileAttributes.Normal, null, true, !string.Equals(path, root.FullName, StringComparison.OrdinalIgnoreCase)));
            var volume = new VolumeDescriptor(VolumeId.Create("V"), "exFAT", [root.FullName], false, true, false, false, true);
            var reader = new WindowsVolumeSnapshotReader(native);

            var events = await ReadAllAsync(reader.ReadInitialSnapshotAsync(volume, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

            Assert.Contains(events, value => value.Name == "denied.txt" && value.Quality == EventQuality.ExistenceOnly);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task CancellationStopsSnapshotWithoutPartialRecoveryEvent()
    {
        var root = Directory.CreateTempSubdirectory("storage-chronicle-cancel");
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var volume = new VolumeDescriptor(VolumeId.Create("V"), "FAT32", [root.FullName], false, false, false, false, true);
            var reader = new WindowsVolumeSnapshotReader(new FakeFileNative());

            #pragma warning disable xUnit1051
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await ReadAllAsync(reader.ReadInitialSnapshotAsync(volume, cancellation.Token), cancellation.Token));
            #pragma warning restore xUnit1051
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static async Task<List<SourceEvent>> ReadAllAsync(IAsyncEnumerable<SourceEvent> source, CancellationToken cancellationToken)
    {
        var result = new List<SourceEvent>();
        await foreach (var item in source.WithCancellation(cancellationToken)) result.Add(item);
        return result;
    }

    private sealed class ReplacedPathFileNative(string rootPath) : IWindowsFileMetadataNative
    {
        private readonly Dictionary<nint, string> opened = [];
        private int nextHandle = 10;
        public List<string> OpenedPaths { get; } = [];

        public SafeFileHandle OpenDirectory(string path)
        {
            OpenedPaths.Add(path);
            if (OpenedPaths.Count > 1) throw new IOException("The queued final component was replaced by a reparse point before it could be opened.");
            var handle = new SafeFileHandle(new IntPtr(nextHandle++), ownsHandle: false);
            opened.Add(handle.DangerousGetHandle(), path);
            return handle;
        }

        public IEnumerable<NativeDirectoryEntry> EnumerateDirectory(SafeFileHandle directoryHandle)
        {
            Assert.Equal(rootPath, opened[directoryHandle.DangerousGetHandle()]);
            return [new NativeDirectoryEntry("candidate", FileAttributes.Directory)];
        }

        public NativeFileMetadataRecord ReadMetadata(string path, string? parentPath = null) => CreateMetadata(path);
        public NativeFileMetadataRecord ReadMetadata(SafeFileHandle handle, string path, string? parentPath = null) => CreateMetadata(path);

        private NativeFileMetadataRecord CreateMetadata(string path)
        {
            var isRoot = string.Equals(path, rootPath, StringComparison.OrdinalIgnoreCase);
            var name = isRoot ? Path.GetFileName(rootPath) : Path.GetFileName(path);
            return new NativeFileMetadataRecord(FileId.Create(isRoot ? "root" : "candidate"), isRoot ? null : FileId.Create("root"), name, FileKind.Directory, null, null, null, null, null, null, FileAttributes.Directory, null, true, false);
        }
    }
}
