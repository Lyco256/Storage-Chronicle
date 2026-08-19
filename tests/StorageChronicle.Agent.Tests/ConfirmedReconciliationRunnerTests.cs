using System.Collections.Immutable;
using Microsoft.Win32.SafeHandles;
using StorageChronicle.Agent;
using StorageChronicle.Contracts;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Normalization;
using StorageChronicle.Platform.Windows.FileSystem.Interop;
using StorageChronicle.Platform.Windows.FileSystem.Snapshot;
using StorageChronicle.Platform.Windows.Ntfs;
using StorageChronicle.Storage;
using Xunit;

namespace StorageChronicle.Agent.Tests;

public sealed class ConfirmedReconciliationRunnerTests
{
    [Fact]
    public async Task ConfirmedNonNtfsScanAppendsOnlyCurrentDifferencesWithReconciliationQuality()
    {
        var root = Path.Combine(Path.GetTempPath(), "StorageChronicle.Reconciliation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var oldPath = Path.Combine(root, "old.txt");
        await File.WriteAllTextAsync(oldPath, "test");
        var newPath = Path.Combine(root, "new.txt");
        try
        {
            var volume = new VolumeDescriptor(VolumeId.Create("test-volume"), "FAT32", [root], false, true, false, false, true);
            var native = new FakeMetadataNative();
            await using var storage = new AppendOnlyStorageEngine(new StorageEngineOptions(Path.Combine(root, "history")) { FlushInterval = TimeSpan.FromMinutes(1) });
            var normalizer = new EventNormalizer();
            var oldMetadata = native.ReadMetadata(oldPath, root);
            var seed = Source(volume.Id, oldMetadata, CanonicalOperation.Create, 1, EventOrigin.LiveUsn);
            var seedCanonical = normalizer.Normalize(seed)!;
            await storage.AppendSourceAsync(seed);
            await storage.AppendCanonicalAsync(seedCanonical);
            await storage.ApplyAsync(seedCanonical);
            await File.WriteAllTextAsync(newPath, "new");

            var health = new AgentHealthState();
            var runner = new ConfirmedReconciliationRunner(
                new FakeVolumes(volume),
                new UnsupportedNtfsApi(),
                new WindowsVolumeSnapshotReader(native),
                new WindowsFileMetadataReader(native),
                storage,
                normalizer,
                health);
            var request = new PendingReconciliationRequest("gap", volume.Id, "test gap", 1, DateTimeOffset.UtcNow.AddMinutes(-1));

            var summary = await runner.ExecuteAsync(request);
            var events = new List<CanonicalEvent>();
            var offset = 0;
            while (true)
            {
                var page = await storage.ReadCanonicalPageAsync(offset, 512);
                if (page.Count == 0) break;
                events.AddRange(page);
                offset += page.Count;
                if (page.Count < 512) break;
            }

            Assert.True(summary.Completed, summary.FailureReason ?? summary.Status);
            Assert.True(summary.CandidateCount >= 1);
            Assert.True(summary.DetailedMetadataQueryCount >= 1);
            Assert.True(events.Count > 0, $"Canonical events={events.Count}; durable={summary.DurableEventCount}");
            Assert.Contains(events, value => value.Origin == EventOrigin.DirectoryReconciliation && value.Operation == CanonicalOperation.ReconciliationDiscovered && value.Quality == EventQuality.Reconciled && value.ProcessQuality == ProcessAttributionQuality.Unknown);
            Assert.DoesNotContain(events, value => value.Origin == EventOrigin.DirectoryReconciliation && value.Properties.ContainsKey("fileContents"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeclaredCancellationDoesNotPretendToComplete()
    {
        var root = Path.Combine(Path.GetTempPath(), "StorageChronicle.Reconciliation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var volume = new VolumeDescriptor(VolumeId.Create("cancel-volume"), "FAT32", [root], false, true, false, false, true);
            await using var storage = new AppendOnlyStorageEngine(new StorageEngineOptions(Path.Combine(root, "history")) { FlushInterval = TimeSpan.FromMinutes(1) });
            var runner = new ConfirmedReconciliationRunner(new FakeVolumes(volume), new UnsupportedNtfsApi(), new WindowsVolumeSnapshotReader(new FakeMetadataNative()), new WindowsFileMetadataReader(new FakeMetadataNative()), storage, new EventNormalizer(), new AgentHealthState());
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var summary = await runner.ExecuteAsync(new PendingReconciliationRequest("gap", volume.Id, "test", 1, DateTimeOffset.UtcNow), cancellation.Token);
            Assert.False(summary.Completed);
            Assert.Equal("Interrupted", summary.Status);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnchangedNonNtfsSnapshotDoesNotQueryDetailedMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "StorageChronicle.Reconciliation", Guid.NewGuid().ToString("N"));
        var historyRoot = Path.Combine(Path.GetTempPath(), "StorageChronicle.ReconciliationHistory", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var filePath = Path.Combine(root, "stable.txt");
        await File.WriteAllTextAsync(filePath, "stable");
        try
        {
            var volume = new VolumeDescriptor(VolumeId.Create("stable-volume"), "FAT32", [root], false, true, false, false, true);
            var native = new FakeMetadataNative();
            await using var storage = new AppendOnlyStorageEngine(new StorageEngineOptions(historyRoot) { FlushInterval = TimeSpan.FromMinutes(1) });
            var normalizer = new EventNormalizer();
            foreach (var path in new[] { root, filePath })
            {
                var metadata = native.ReadMetadata(path, path == root ? null : root);
                var source = Source(volume.Id, metadata, metadata.Kind == FileKind.Directory ? CanonicalOperation.DirectoryCreate : CanonicalOperation.Create, metadata.Kind == FileKind.Directory ? 1 : 2, EventOrigin.InitialSnapshot);
                var canonical = normalizer.Normalize(source)!;
                await storage.AppendSourceAsync(source);
                await storage.AppendCanonicalAsync(canonical);
                await storage.ApplyAsync(canonical);
            }

            var runner = new ConfirmedReconciliationRunner(new FakeVolumes(volume), new UnsupportedNtfsApi(), new WindowsVolumeSnapshotReader(native), new WindowsFileMetadataReader(native), storage, normalizer, new AgentHealthState());
            var summary = await runner.ExecuteAsync(new PendingReconciliationRequest("stable-gap", volume.Id, "test gap", 2, DateTimeOffset.UtcNow.AddMinutes(-1)));

            Assert.True(summary.Completed);
            Assert.Equal(0, summary.DetailedMetadataQueryCount);
            Assert.Equal(0, summary.DurableEventCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(historyRoot)) Directory.Delete(historyRoot, recursive: true);
        }
    }

    [Fact]
    public async Task NonNtfsSnapshotGapIsFailedAndRecordedInsteadOfCompleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "StorageChronicle.Reconciliation", Guid.NewGuid().ToString("N"));
        var historyRoot = Path.Combine(Path.GetTempPath(), "StorageChronicle.ReconciliationHistory", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var volume = new VolumeDescriptor(VolumeId.Create("gap-volume"), "FAT32", [root], false, true, false, false, true);
        try
        {
            Directory.Delete(root);
            await using var storage = new AppendOnlyStorageEngine(new StorageEngineOptions(historyRoot) { FlushInterval = TimeSpan.FromMinutes(1) });
            var runner = new ConfirmedReconciliationRunner(new FakeVolumes(volume), new UnsupportedNtfsApi(), new WindowsVolumeSnapshotReader(new FakeMetadataNative()), new WindowsFileMetadataReader(new FakeMetadataNative()), storage, new EventNormalizer(), new AgentHealthState());

            var summary = await runner.ExecuteAsync(new PendingReconciliationRequest("gap-snapshot", volume.Id, "snapshot gap", 1, DateTimeOffset.UtcNow.AddMinutes(-1)));
            var events = new List<CanonicalEvent>();
            var offset = 0;
            while (true)
            {
                var page = await storage.ReadCanonicalPageAsync(offset, 512);
                if (page.Count == 0) break;
                events.AddRange(page);
                offset += page.Count;
                if (page.Count < 512) break;
            }

            Assert.False(summary.Completed);
            Assert.Equal("Failed", summary.Status);
            Assert.Contains(events, value => value.Operation == CanonicalOperation.UnverifiedGap && value.Quality == EventQuality.UnverifiedGap);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(historyRoot)) Directory.Delete(historyRoot, recursive: true);
        }
    }

    private static SourceEvent Source(VolumeId volume, NativeFileMetadataRecord metadata, CanonicalOperation operation, long sequence, EventOrigin origin) =>
        new(EventId.New(), EventSchemaVersion.Current, origin, volume, metadata.FileId, metadata.ParentFileId, metadata.Name, null, operation,
            new FileMetadata(volume, metadata.FileId, metadata.ParentFileId, metadata.Name, metadata.Kind, metadata.LogicalSize, metadata.AllocatedSize, metadata.CreatedUtc, metadata.LastAccessUtc, metadata.LastWriteUtc, metadata.FileSystemChangeUtc, metadata.Attributes, metadata.ReparsePointKind, null, EventQuality.Exact, true, false),
            new EventTime(DateTimeOffset.UtcNow, TimeSpan.Zero, null, DateTimeOffset.UtcNow, new SourceSequence(sequence), new MountSequence(sequence)), EventQuality.Exact, null, ProcessAttributionQuality.Unknown, null, null, ImmutableDictionary<string, string>.Empty);

    private sealed class FakeVolumes(VolumeDescriptor volume) : IVolumeEnumerator
    {
        public ValueTask<IReadOnlyList<VolumeDescriptor>> EnumerateAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<VolumeDescriptor>>([volume]);
    }

    private sealed class UnsupportedNtfsApi : StorageChronicle.Platform.Windows.Ntfs.INtfsApi
    {
        public SafeFileHandle OpenVolume(string devicePath) => throw new PlatformNotSupportedException();
        public NtfsApiCallResult QueryUsnJournal(SafeFileHandle volumeHandle, out UsnJournalData? data) { data = null; throw new PlatformNotSupportedException(); }
        public NtfsApiCallResult ReadUsnJournal(SafeFileHandle volumeHandle, ReadUsnJournalRequest request, byte[] outputBuffer, out int bytesReturned) { bytesReturned = 0; throw new PlatformNotSupportedException(); }
        public NtfsApiCallResult EnumerateUsnData(SafeFileHandle volumeHandle, EnumUsnDataRequest request, byte[] outputBuffer, out int bytesReturned) { bytesReturned = 0; throw new PlatformNotSupportedException(); }
    }

    private sealed class FakeMetadataNative : IWindowsFileMetadataNative
    {
        public NativeFileMetadataRecord ReadMetadata(string path, string? parentPath = null)
        {
            var attributes = File.GetAttributes(path);
            var info = new FileInfo(path);
            var kind = (attributes & FileAttributes.Directory) != 0 ? FileKind.Directory : FileKind.File;
            return new NativeFileMetadataRecord(FileId.Create("fake:" + Path.GetFullPath(path)), string.IsNullOrWhiteSpace(parentPath) ? null : FileId.Create("fake:" + Path.GetFullPath(parentPath)), info.Name, kind, kind == FileKind.Directory ? null : info.Length, null, info.CreationTimeUtc, info.LastAccessTimeUtc, info.LastWriteTimeUtc, info.LastWriteTimeUtc, attributes, null, true, false);
        }

        public SafeFileHandle OpenDirectory(string path) => new(new IntPtr(-1), ownsHandle: false);
    }
}
