using System.Collections.Immutable;
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
}
