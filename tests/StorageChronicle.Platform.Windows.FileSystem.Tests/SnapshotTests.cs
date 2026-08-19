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
    public async Task InitialSnapshotYieldsConfiguredBatchSizeDuringOneDirectory()
    {
        var root = Directory.CreateTempSubdirectory("storage-chronicle-snapshot-batch");
        try
        {
            for (var index = 0; index < 5; index++) using (File.Create(Path.Combine(root.FullName, $"entry-{index}.txt"))) { }
            using var cancellation = new CancellationTokenSource();
            var calls = 0;
            var volume = new VolumeDescriptor(VolumeId.Create("V"), "exFAT", [root.FullName], false, true, false, false, true);
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
}
