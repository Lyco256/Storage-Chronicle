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
    public async Task SnapshotOpensEveryDescendantRelativeToItsPinnedParentHandle()
    {
        var root = Directory.CreateTempSubdirectory("storage-chronicle-snapshot-relative");
        try
        {
            var childDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "child"));
            File.WriteAllText(Path.Combine(childDirectory.FullName, "visible.txt"), "not read by collector");
            var native = new FakeFileNative(path =>
            {
                var attributes = File.GetAttributes(path);
                var kind = (attributes & FileAttributes.ReparsePoint) != 0
                    ? FileKind.ReparsePoint
                    : (attributes & FileAttributes.Directory) != 0 ? FileKind.Directory : FileKind.File;
                return new NativeFileMetadataRecord(FileId.Create("id:" + path), null, Path.GetFileName(path), kind,
                    kind == FileKind.File ? new FileInfo(path).Length : null, null, null, null, null, null, attributes,
                    kind == FileKind.ReparsePoint ? "ReparsePoint" : null, true, false);
            });
            var volume = new VolumeDescriptor(VolumeId.Create("V"), "FAT32", [root.FullName], false, false, ProtectedVolumeRoles.None, false, true);
            var reader = new WindowsVolumeSnapshotReader(native);

            var events = await ReadAllAsync(reader.ReadInitialSnapshotAsync(volume, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

            Assert.Contains(events, value => value.Name == "child" && value.Metadata?.Kind == FileKind.Directory);
            Assert.Contains(events, value => value.Name == "visible.txt" && value.Metadata?.Kind == FileKind.File);
            Assert.Single(native.OpenedPaths);
            Assert.Equal(2, native.OpenedChildren.Count);
            Assert.Equal(0, native.PathMetadataReadCount);
            Assert.NotEqual(native.OpenedChildren[0].Parent, native.OpenedChildren[1].Parent);
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
            var volume = new VolumeDescriptor(VolumeId.Create(@"\\?\Volume{ABC}"), "NTFS", [mount.FullName], false, false, ProtectedVolumeRoles.None, true, true);
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
    public async Task ConfiguredSubdirectoryIsReachedFromTheVolumeRootByPinnedHandles()
    {
        var mount = Directory.CreateTempSubdirectory("storage-chronicle-scoped-volume");
        try
        {
            var selected = Directory.CreateDirectory(Path.Combine(mount.FullName, "selected"));
            File.WriteAllText(Path.Combine(selected.FullName, "included.txt"), "fixture only");
            Directory.CreateDirectory(Path.Combine(mount.FullName, "outside"));
            var native = new FakeFileNative(path =>
            {
                var attributes = File.GetAttributes(path);
                var kind = (attributes & FileAttributes.Directory) != 0 ? FileKind.Directory : FileKind.File;
                return new NativeFileMetadataRecord(FileId.Create("id:" + path), null, Path.GetFileName(path), kind,
                    kind == FileKind.File ? new FileInfo(path).Length : null, null, null, null, null, null, attributes, null, true, false);
            });
            var options = new WindowsFileSystemOptions { MonitoredRoots = [selected.FullName] };
            var volume = new VolumeDescriptor(VolumeId.Create("V"), "FAT32", [mount.FullName], false, false, ProtectedVolumeRoles.None, false, true);
            var reader = new WindowsVolumeSnapshotReader(native, new WindowsExclusionPolicy(options), options);

            var events = await ReadAllAsync(reader.ReadInitialSnapshotAsync(volume, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

            Assert.Contains(events, value => value.Name == "included.txt");
            Assert.DoesNotContain(events, value => value.Name == "outside");
            Assert.Equal(mount.FullName, Assert.Single(native.OpenedPaths));
            Assert.Contains(native.OpenedChildren, value => value.Name == "selected");
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
            var volume = new VolumeDescriptor(VolumeId.Create("V"), "NTFS", [root.FullName], false, false, ProtectedVolumeRoles.None, true, true);
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
    [Trait("Category", "WindowsApi")]
    public async Task NativeSnapshotDoesNotTraverseDirectoryReparsePointSwappedAfterEnumeration()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("This test requires Windows handle-relative file opens.");
        var sandbox = Directory.CreateTempSubdirectory("storage-chronicle-snapshot-swap");
        var root = Directory.CreateDirectory(Path.Combine(sandbox.FullName, "root"));
        var target = Directory.CreateDirectory(Path.Combine(sandbox.FullName, "target"));
        var candidatePath = Path.Combine(root.FullName, "candidate");
        _ = Directory.CreateDirectory(candidatePath);
        var movedOriginal = Path.Combine(sandbox.FullName, "original-candidate");
        File.WriteAllText(Path.Combine(target.FullName, "outside.txt"), "fixture only; collector must not read content");
        var swapped = false;
        try
        {
            var nativeType = typeof(WindowsVolumeSnapshotReader).Assembly.GetType("StorageChronicle.Platform.Windows.FileSystem.Interop.WindowsNativeApi", throwOnError: true)!;
            var inner = Assert.IsAssignableFrom<IWindowsFileMetadataNative>(Activator.CreateInstance(nativeType, nonPublic: true));
            var native = new SwapDirectoryEntryNative(inner, candidatePath, movedOriginal, target.FullName);
            var volume = new VolumeDescriptor(VolumeId.Create("V"), "NTFS", [root.FullName], false, false, ProtectedVolumeRoles.None, true, true);
            var reader = new WindowsVolumeSnapshotReader(native);
            var events = await ReadAllAsync(reader.ReadInitialSnapshotAsync(volume, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            if (native.SwapFailed)
            {
                Assert.Skip("The host cannot create an unprivileged directory symbolic link during enumeration.");
                return;
            }

            swapped = native.SwapCompleted;
            Assert.True(swapped, "The test must replace the enumerated entry before its handle-relative open.");
            Assert.Contains(events, value => value.Name == "candidate" && value.Metadata?.Kind == FileKind.SymbolicLink && (value.Metadata.Attributes & FileAttributes.ReparsePoint) != 0);
            Assert.DoesNotContain(events, value => value.Name == "outside.txt");
        }
        finally
        {
            if (Directory.Exists(Path.Combine(root.FullName, "candidate"))) Directory.Delete(Path.Combine(root.FullName, "candidate"));
            if (Directory.Exists(movedOriginal)) Directory.Delete(movedOriginal, recursive: true);
            sandbox.Delete(true);
        }
    }

    [Fact]
    public async Task InitialSnapshotRecordsReparsePointButDoesNotTraverseIt()
    {
        var root = CreateFixture();
        try
        {
            ValidateFixture(root);
            Directory.CreateDirectory(Path.Combine(root.FullName, "junction-loop", "hidden"));
            WriteFixtureFile(root, Path.Combine(root.FullName, "visible.txt"), "not read by collector");
            var native = new FakeFileNative(path =>
            {
                var name = Path.GetFileName(path);
                if (name == "junction-loop") return new NativeFileMetadataRecord(FileId.Create("junction"), FileId.Create("root"), name, FileKind.Junction, null, null, null, null, null, null, FileAttributes.Directory | FileAttributes.ReparsePoint, "Junction", true, false);
                return new NativeFileMetadataRecord(FileId.Create(name == root.Name ? "root" : name), FileId.Create("root"), name, name == root.Name ? FileKind.Directory : FileKind.File, 1, null, null, null, null, null, name == root.Name ? FileAttributes.Directory : FileAttributes.Normal, null, true, false);
            });
            var volume = new VolumeDescriptor(VolumeId.Create("V"), "FAT32", [root.FullName], false, true, ProtectedVolumeRoles.None, false, true);
            var reader = new WindowsVolumeSnapshotReader(native, new WindowsExclusionPolicy(), new WindowsFileSystemOptions { SnapshotBatchSize = 1 });

            var events = await ReadAllAsync(reader.ReadInitialSnapshotAsync(volume, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

            Assert.Contains(events, value => value.Metadata?.Kind == FileKind.Junction);
            Assert.DoesNotContain(events, value => value.Name == "hidden");
            Assert.All(events, value => Assert.DoesNotContain(value.Properties.Keys, key => key.Contains("content", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    [Fact]
    public async Task AccessDeniedMetadataFallsBackToExistenceOnly()
    {
        var root = CreateFixture();
        try
        {
            var denied = Path.Combine(root.FullName, "denied.txt");
            WriteFixtureFile(root, denied, "placeholder");
            var native = new FakeFileNative(path => new NativeFileMetadataRecord(FileId.Create("id:" + path), null, Path.GetFileName(path), FileKind.File, null, null, null, null, null, null, FileAttributes.Normal, null, true, !string.Equals(path, root.FullName, StringComparison.OrdinalIgnoreCase)));
            var volume = new VolumeDescriptor(VolumeId.Create("V"), "exFAT", [root.FullName], false, true, ProtectedVolumeRoles.None, false, true);
            var reader = new WindowsVolumeSnapshotReader(native);

            var events = await ReadAllAsync(reader.ReadInitialSnapshotAsync(volume, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

            Assert.Contains(events, value => value.Name == "denied.txt" && value.Quality == EventQuality.ExistenceOnly);
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    [Fact]
    public async Task InitialSnapshotYieldsConfiguredBatchSizeDuringOneDirectory()
    {
        var root = Directory.CreateTempSubdirectory("storage-chronicle-snapshot-batch");
        try
        {
            for (var index = 0; index < 5; index++) using (File.Create(Path.Combine(root.FullName, $"entry-{index}.txt"))) { }
            using var cancellation = new CancellationTokenSource();
            var calls = 0;
            var volume = new VolumeDescriptor(VolumeId.Create("V"), "exFAT", [root.FullName], false, true, ProtectedVolumeRoles.None, false, true);
            var native = new FakeFileNative(path =>
            {
                var value = new NativeFileMetadataRecord(
                    FileId.Create("id:" + path),
                    FileId.Create("root"),
                    Path.GetFileName(path),
                    string.Equals(path, root.FullName, StringComparison.OrdinalIgnoreCase) ? FileKind.Directory : FileKind.File,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    FileAttributes.Normal,
                    null,
                    true,
                    false);
                if (Interlocked.Increment(ref calls) == 2) cancellation.Cancel();
                return value;
            });
            var reader = new WindowsVolumeSnapshotReader(native, new WindowsExclusionPolicy(), new WindowsFileSystemOptions { SnapshotBatchSize = 2 });
            var enumerator = reader.ReadInitialSnapshotAsync(volume, cancellation.Token).GetAsyncEnumerator(cancellation.Token);

            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(2, calls);
            Assert.True(await enumerator.MoveNextAsync());
            #pragma warning disable xUnit1051
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
            #pragma warning restore xUnit1051
            await enumerator.DisposeAsync();
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task CancellationStopsSnapshotWithoutPartialRecoveryEvent()
    {
        var root = CreateFixture();
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var volume = new VolumeDescriptor(VolumeId.Create("V"), "FAT32", [root.FullName], false, false, ProtectedVolumeRoles.None, false, true);
            var reader = new WindowsVolumeSnapshotReader(new FakeFileNative());

            #pragma warning disable xUnit1051
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await ReadAllAsync(reader.ReadInitialSnapshotAsync(volume, cancellation.Token), cancellation.Token));
            #pragma warning restore xUnit1051
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    private static async Task<List<SourceEvent>> ReadAllAsync(IAsyncEnumerable<SourceEvent> source, CancellationToken cancellationToken)
    {
        var result = new List<SourceEvent>();
        await foreach (var item in source.WithCancellation(cancellationToken)) result.Add(item);
        return result;
    }

    private static DirectoryInfo CreateFixture()
    {
        var runId = Guid.NewGuid().ToString("N");
        var path = Path.Combine(Path.GetTempPath(), "StorageChronicle.WindowsFileSystem.Tests", runId);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, ".test-owner"), runId);
        return new DirectoryInfo(path);
    }

    private static void WriteFixtureFile(DirectoryInfo root, string path, string content)
    {
        ValidateFixture(root);
        var target = Path.GetFullPath(path);
        var boundary = Path.GetFullPath(root.FullName).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) || (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidOperationException("Refusing to write outside the owned snapshot fixture.");
        File.WriteAllText(target, content);
    }

    private static void DeleteFixture(DirectoryInfo root)
    {
        ValidateFixture(root);
        Directory.Delete(root.FullName, recursive: true);
    }

    private static void ValidateFixture(DirectoryInfo root)
    {
        var target = Path.GetFullPath(root.FullName);
        var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "StorageChronicle.WindowsFileSystem.Tests"));
        var runId = Path.GetFileName(target);
        if (!string.Equals(Path.GetDirectoryName(target), allowedRoot, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(runId, "N", out _))
            throw new InvalidOperationException("Refusing to modify a snapshot fixture outside its run-specific root.");
        var marker = Path.Combine(target, ".test-owner");
        if (!File.Exists(marker) || !string.Equals(File.ReadAllText(marker), runId, StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to modify a snapshot fixture without its run owner marker.");
        var boundary = target.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in Directory.EnumerateFileSystemEntries(target, "*", SearchOption.AllDirectories))
        {
            var resolved = Path.GetFullPath(entry);
            if (!resolved.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing to modify a snapshot fixture with an out-of-root path or reparse point.");
        }
    }

    private sealed class SwapDirectoryEntryNative(IWindowsFileMetadataNative inner, string candidatePath, string movedOriginalPath, string targetPath) : IWindowsFileMetadataNative
    {
        private int swapState;
        public bool SwapCompleted => Volatile.Read(ref swapState) == 1;
        public bool SwapFailed => Volatile.Read(ref swapState) == 2;

        public NativeFileMetadataRecord ReadMetadata(string path, string? parentPath = null) => inner.ReadMetadata(path, parentPath);
        public NativeFileMetadataRecord ReadMetadata(SafeFileHandle handle, string path, string? parentPath = null) => inner.ReadMetadata(handle, path, parentPath);
        public NativeFileMetadataRecord ReadMetadataRelative(SafeFileHandle handle, string path, SafeFileHandle? parentDirectoryHandle) => inner.ReadMetadataRelative(handle, path, parentDirectoryHandle);
        public IEnumerable<NativeDirectoryEntry> EnumerateDirectory(SafeFileHandle directoryHandle)
        {
            foreach (var entry in inner.EnumerateDirectory(directoryHandle))
            {
                if (entry.Name.Equals("candidate", StringComparison.OrdinalIgnoreCase) && Interlocked.CompareExchange(ref swapState, -1, 0) == 0)
                {
                    try
                    {
                        Directory.Move(candidatePath, movedOriginalPath);
                        Directory.CreateSymbolicLink(candidatePath, targetPath);
                        Volatile.Write(ref swapState, 1);
                    }
                    catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
                    {
                        Volatile.Write(ref swapState, 2);
                        throw;
                    }
                }

                yield return entry;
            }
        }

        public SafeFileHandle OpenMetadata(string path, bool directory) => inner.OpenMetadata(path, directory);
        public SafeFileHandle OpenDirectory(string path) => inner.OpenDirectory(path);
        public SafeFileHandle OpenChild(SafeFileHandle parentDirectoryHandle, string childName, FileAttributes enumeratedAttributes) => inner.OpenChild(parentDirectoryHandle, childName, enumeratedAttributes);
    }
}
