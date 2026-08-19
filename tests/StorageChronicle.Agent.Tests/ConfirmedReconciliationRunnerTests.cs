using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
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
            Assert.True(summary.DetailedQueryCandidateRatio > 0d);
            Assert.True(summary.ElapsedMilliseconds >= 0d);
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
    public async Task MissingVolumeIsRecordedAsFailedGapInsteadOfEscapingBeforeFailureHandling()
    {
        var root = Path.Combine(Path.GetTempPath(), "StorageChronicle.ReconciliationHistory", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var volumeId = VolumeId.Create("detached-volume");
            await using var storage = new AppendOnlyStorageEngine(new StorageEngineOptions(root) { FlushInterval = TimeSpan.FromMinutes(1) });
            var runner = new ConfirmedReconciliationRunner(
                new FakeVolumes(),
                new UnsupportedNtfsApi(),
                new WindowsVolumeSnapshotReader(new FakeMetadataNative()),
                new WindowsFileMetadataReader(new FakeMetadataNative()),
                storage,
                new EventNormalizer(),
                new AgentHealthState());

            var summary = await runner.ExecuteAsync(new PendingReconciliationRequest("detached-gap", volumeId, "volume detached", 1, DateTimeOffset.UtcNow.AddMinutes(-1), FileSystem: "NTFS"));
            var events = new List<CanonicalEvent>();
            var page = await storage.ReadCanonicalPageAsync(0, 512);
            events.AddRange(page);

            Assert.False(summary.Completed);
            Assert.Equal("Failed", summary.Status);
            Assert.Equal("NTFS", summary.FileSystem);
            Assert.Contains(events, value => value.Operation == CanonicalOperation.UnverifiedGap && value.Quality == EventQuality.UnverifiedGap);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task VolumeEnumerationFailureIsRecordedAsFailedGapInsteadOfEscaping()
    {
        var root = Path.Combine(Path.GetTempPath(), "StorageChronicle.ReconciliationHistory", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var volumeId = VolumeId.Create("enumeration-failure-volume");
            await using var storage = new AppendOnlyStorageEngine(new StorageEngineOptions(root) { FlushInterval = TimeSpan.FromMinutes(1) });
            var runner = new ConfirmedReconciliationRunner(
                new ThrowingVolumes(),
                new UnsupportedNtfsApi(),
                new WindowsVolumeSnapshotReader(new FakeMetadataNative()),
                new WindowsFileMetadataReader(new FakeMetadataNative()),
                storage,
                new EventNormalizer(),
                new AgentHealthState());

            var summary = await runner.ExecuteAsync(new PendingReconciliationRequest("enumeration-gap", volumeId, "volume enumeration failed", 1, DateTimeOffset.UtcNow.AddMinutes(-1), FileSystem: "NTFS"));
            var events = await storage.ReadCanonicalPageAsync(0, 512);

            Assert.False(summary.Completed);
            Assert.Equal("Failed", summary.Status);
            Assert.Contains(events, value => value.Operation == CanonicalOperation.UnverifiedGap && value.Quality == EventQuality.UnverifiedGap);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingNtfsCompletionBoundaryIsRecordedAsFailedGapInsteadOfCompleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "StorageChronicle.ReconciliationHistory", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var volume = new VolumeDescriptor(VolumeId.Create("boundary-volume"), "NTFS", [root], false, false, false, true, true);
            var fileId = FileId.Create("0001000000000001");
            var parentId = FileId.Create("0000000000000005");
            var metadata = new FileMetadata(volume.Id, fileId, parentId, "stable.txt", FileKind.File, null, null, null, null, null, null, FileAttributes.Normal, null, null, EventQuality.Exact, true, false);
            await using var storage = new AppendOnlyStorageEngine(new StorageEngineOptions(root) { FlushInterval = TimeSpan.FromMinutes(1) });
            var normalizer = new EventNormalizer();
            var seed = new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.InitialSnapshot, volume.Id, fileId, parentId, "stable.txt", null, CanonicalOperation.Create,
                metadata,
                new EventTime(DateTimeOffset.UtcNow, TimeSpan.Zero, null, DateTimeOffset.UtcNow, new SourceSequence(7), new MountSequence(7)),
                EventQuality.Exact, null, ProcessAttributionQuality.Unknown, null, null, ImmutableDictionary<string, string>.Empty);
            var canonical = normalizer.Normalize(seed)!;
            await storage.AppendSourceAsync(seed);
            await storage.AppendCanonicalAsync(canonical);
            await storage.ApplyAsync(canonical);

            var api = new PostScanBoundaryFailureNtfsApi();
            var runner = new ConfirmedReconciliationRunner(
                new FakeVolumes(volume),
                api,
                new WindowsVolumeSnapshotReader(new FakeMetadataNative()),
                new WindowsFileMetadataReader(new FakeMetadataNative()),
                storage,
                normalizer,
                new AgentHealthState());

            var summary = await runner.ExecuteAsync(new PendingReconciliationRequest("boundary-gap", volume.Id, "boundary test", 7, DateTimeOffset.UtcNow.AddMinutes(-1), FileSystem: "NTFS"));
            var events = await storage.ReadCanonicalPageAsync(0, 512);

            Assert.False(summary.Completed);
            Assert.Equal("Failed", summary.Status);
            Assert.Equal(2, api.QueryCount);
            Assert.Contains(events, value => value.Operation == CanonicalOperation.UnverifiedGap && value.Quality == EventQuality.UnverifiedGap);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AccessDeniedCandidateRecordsAclFallbackQualityAndCounter()
    {
        var root = Path.Combine(Path.GetTempPath(), "StorageChronicle.ReconciliationHistory", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var volume = new VolumeDescriptor(VolumeId.Create("acl-fallback-volume"), "NTFS", [root], false, false, false, true, true);
            var fileId = FileId.Create("0001000000000001");
            var parentId = FileId.Create("0000000000000005");
            await using var storage = new AppendOnlyStorageEngine(new StorageEngineOptions(root) { FlushInterval = TimeSpan.FromMinutes(1) });
            var normalizer = new EventNormalizer();
            var seedMetadata = new FileMetadata(volume.Id, fileId, parentId, "stable.txt", FileKind.File, null, null, null, null, null, null, FileAttributes.Normal, null, null, EventQuality.Exact, true, false);
            var seed = new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.InitialSnapshot, volume.Id, fileId, parentId, "stable.txt", null, CanonicalOperation.Create,
                seedMetadata,
                new EventTime(DateTimeOffset.UtcNow, TimeSpan.Zero, null, DateTimeOffset.UtcNow, new SourceSequence(7), new MountSequence(7)),
                EventQuality.Exact, null, ProcessAttributionQuality.Unknown, null, null, ImmutableDictionary<string, string>.Empty);
            var canonical = normalizer.Normalize(seed)!;
            await storage.AppendSourceAsync(seed);
            await storage.AppendCanonicalAsync(canonical);
            await storage.ApplyAsync(canonical);

            var runner = new ConfirmedReconciliationRunner(
                new FakeVolumes(volume),
                new StableNtfsApi(),
                new WindowsVolumeSnapshotReader(new AccessDeniedMetadataNative()),
                new WindowsFileMetadataReader(new AccessDeniedMetadataNative()),
                storage,
                normalizer,
                new AgentHealthState());

            var summary = await runner.ExecuteAsync(new PendingReconciliationRequest("acl-fallback-gap", volume.Id, "ACL fallback test", 7, DateTimeOffset.UtcNow.AddMinutes(-1), FileSystem: "NTFS"));

            Assert.True(summary.Completed, summary.FailureReason ?? summary.Status);
            Assert.Equal(1, summary.CandidateCount);
            Assert.Equal(1, summary.DetailedMetadataQueryCount);
            Assert.Equal(1d, summary.DetailedQueryCandidateRatio);
            Assert.Equal(1, summary.AclFallbackCount);
            Assert.Contains(await storage.ReadCanonicalPageAsync(0, 512), value => value.Metadata?.Quality == EventQuality.ExistenceOnly);
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
            Assert.Equal(0d, summary.DetailedQueryCandidateRatio);
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

    [Fact]
    public async Task MissingNtfsStartBoundaryIsFailedBeforeMftEnumeration()
    {
        var root = Path.Combine(Path.GetTempPath(), "StorageChronicle.ReconciliationHistory", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var volume = new VolumeDescriptor(VolumeId.Create("pre-scan-boundary-volume"), "NTFS", [root], false, false, false, true, true);
            var api = new PreScanBoundaryFailureNtfsApi();
            await using var storage = new AppendOnlyStorageEngine(new StorageEngineOptions(root) { FlushInterval = TimeSpan.FromMinutes(1) });
            var runner = new ConfirmedReconciliationRunner(new FakeVolumes(volume), api, new WindowsVolumeSnapshotReader(new FakeMetadataNative()), new WindowsFileMetadataReader(new FakeMetadataNative()), storage, new EventNormalizer(), new AgentHealthState());

            var summary = await runner.ExecuteAsync(new PendingReconciliationRequest("pre-scan-boundary-gap", volume.Id, "missing start boundary", 1, DateTimeOffset.UtcNow.AddMinutes(-1), FileSystem: "NTFS"));
            var events = await storage.ReadCanonicalPageAsync(0, 512);

            Assert.False(summary.Completed);
            Assert.Equal("Failed", summary.Status);
            Assert.Equal(0, api.EnumerationCount);
            Assert.Contains(events, value => value.Operation == CanonicalOperation.UnverifiedGap && value.Quality == EventQuality.UnverifiedGap);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LiveEventCoveredByNtfsCompletionBoundaryIsClassifiedAsDeduplicated()
    {
        var root = Path.Combine(Path.GetTempPath(), "StorageChronicle.ReconciliationHistory", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var filePath = Path.Combine(root, "stable.txt");
        await File.WriteAllTextAsync(filePath, "stable");
        try
        {
            var volume = new VolumeDescriptor(VolumeId.Create("live-boundary-volume"), "NTFS", [root], false, false, false, true, true);
            var fileId = FileId.Create("0001000000000001");
            var parentId = FileId.Create("0000000000000005");
            var metadata = new FileMetadata(volume.Id, fileId, parentId, "stable.txt", FileKind.File, null, null, null, null, null, null, FileAttributes.Normal, null, null, EventQuality.Exact, true, false);
            var seed = new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.InitialSnapshot, volume.Id, fileId, parentId, "stable.txt", null, CanonicalOperation.Create,
                metadata, new EventTime(DateTimeOffset.UtcNow, TimeSpan.Zero, null, DateTimeOffset.UtcNow, new SourceSequence(7), new MountSequence(7)), EventQuality.Exact, null, ProcessAttributionQuality.Unknown, null, null, ImmutableDictionary<string, string>.Empty);
            var live = seed with
            {
                EventId = EventId.New(),
                Origin = EventOrigin.LiveUsn,
                Hint = CanonicalOperation.DataWrite,
                Time = seed.Time with { SourceSequence = new SourceSequence(50), MountSequence = new MountSequence(50), RecordedUtc = DateTimeOffset.UtcNow }
            };
            var buffer = new ReconciliationLiveEventBuffer();
            await using var storage = new AppendOnlyStorageEngine(new StorageEngineOptions(root) { FlushInterval = TimeSpan.FromMinutes(1) });
            var normalizer = new EventNormalizer();
            var canonical = normalizer.Normalize(seed)!;
            await storage.AppendSourceAsync(seed);
            await storage.AppendCanonicalAsync(canonical);
            await storage.ApplyAsync(canonical);

            var runner = new ConfirmedReconciliationRunner(new FakeVolumes(volume), new StableNtfsApi(() => buffer.Observe(live), 100), new WindowsVolumeSnapshotReader(new FakeMetadataNative()), new WindowsFileMetadataReader(new FakeMetadataNative()), storage, normalizer, new AgentHealthState(), buffer);
            var summary = await runner.ExecuteAsync(new PendingReconciliationRequest("live-boundary-gap", volume.Id, "live overlap", 7, DateTimeOffset.UtcNow.AddMinutes(-1), FileSystem: "NTFS"));

            Assert.True(summary.Completed, summary.FailureReason ?? summary.Status);
            Assert.Equal(1, summary.LiveEventCount);
            Assert.Equal(1, summary.LiveEventsDeduplicated);
            Assert.Equal(0, summary.LiveEventsAccepted);
            Assert.NotNull(summary.StartJournalBoundary);
            Assert.NotNull(summary.CompletionJournalBoundary);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static SourceEvent Source(VolumeId volume, NativeFileMetadataRecord metadata, CanonicalOperation operation, long sequence, EventOrigin origin) =>
        new(EventId.New(), EventSchemaVersion.Current, origin, volume, metadata.FileId, metadata.ParentFileId, metadata.Name, null, operation,
            new FileMetadata(volume, metadata.FileId, metadata.ParentFileId, metadata.Name, metadata.Kind, metadata.LogicalSize, metadata.AllocatedSize, metadata.CreatedUtc, metadata.LastAccessUtc, metadata.LastWriteUtc, metadata.FileSystemChangeUtc, metadata.Attributes, metadata.ReparsePointKind, null, EventQuality.Exact, true, false),
            new EventTime(DateTimeOffset.UtcNow, TimeSpan.Zero, null, DateTimeOffset.UtcNow, new SourceSequence(sequence), new MountSequence(sequence)), EventQuality.Exact, null, ProcessAttributionQuality.Unknown, null, null, ImmutableDictionary<string, string>.Empty);

    private sealed class FakeVolumes(VolumeDescriptor? volume = null) : IVolumeEnumerator
    {
        public ValueTask<IReadOnlyList<VolumeDescriptor>> EnumerateAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<VolumeDescriptor>>(volume is null ? [] : [volume]);
    }

    private sealed class ThrowingVolumes : IVolumeEnumerator
    {
        public async ValueTask<IReadOnlyList<VolumeDescriptor>> EnumerateAsync(CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new IOException("volume enumeration failed");
        }
    }

    private sealed class UnsupportedNtfsApi : StorageChronicle.Platform.Windows.Ntfs.INtfsApi
    {
        public SafeFileHandle OpenVolume(string devicePath) => throw new PlatformNotSupportedException();
        public NtfsApiCallResult QueryUsnJournal(SafeFileHandle volumeHandle, out UsnJournalData? data) { data = null; throw new PlatformNotSupportedException(); }
        public NtfsApiCallResult ReadUsnJournal(SafeFileHandle volumeHandle, ReadUsnJournalRequest request, byte[] outputBuffer, out int bytesReturned) { bytesReturned = 0; throw new PlatformNotSupportedException(); }
        public NtfsApiCallResult EnumerateUsnData(SafeFileHandle volumeHandle, EnumUsnDataRequest request, byte[] outputBuffer, out int bytesReturned) { bytesReturned = 0; throw new PlatformNotSupportedException(); }
    }

    private sealed class PreScanBoundaryFailureNtfsApi : StorageChronicle.Platform.Windows.Ntfs.INtfsApi
    {
        public int EnumerationCount { get; private set; }

        public SafeFileHandle OpenVolume(string devicePath) => new(new nint(1), ownsHandle: false);

        public NtfsApiCallResult QueryUsnJournal(SafeFileHandle volumeHandle, out UsnJournalData? data)
        {
            data = null;
            return new NtfsApiCallResult(NtfsApiStatus.AccessDenied, 0, 5);
        }

        public NtfsApiCallResult ReadUsnJournal(SafeFileHandle volumeHandle, ReadUsnJournalRequest request, byte[] outputBuffer, out int bytesReturned)
        {
            bytesReturned = 0;
            return new NtfsApiCallResult(NtfsApiStatus.AccessDenied, 0, 5);
        }

        public NtfsApiCallResult EnumerateUsnData(SafeFileHandle volumeHandle, EnumUsnDataRequest request, byte[] outputBuffer, out int bytesReturned)
        {
            EnumerationCount++;
            throw new InvalidOperationException("MFT enumeration must not start without a journal boundary.");
        }
    }

    private sealed class PostScanBoundaryFailureNtfsApi : StorageChronicle.Platform.Windows.Ntfs.INtfsApi
    {
        private readonly byte[] enumBuffer = CreateEnumBuffer();

        public int QueryCount { get; private set; }

        public SafeFileHandle OpenVolume(string devicePath) => new(new nint(1), ownsHandle: false);

        public NtfsApiCallResult QueryUsnJournal(SafeFileHandle volumeHandle, out UsnJournalData? data)
        {
            QueryCount++;
            if (QueryCount == 1)
            {
                data = new UsnJournalData(1, 1, 7, 1, 100, 4096, 4096, 2, 3);
                return new NtfsApiCallResult(NtfsApiStatus.Success, 56, 0);
            }

            data = null;
            return new NtfsApiCallResult(NtfsApiStatus.AccessDenied, 0, 5);
        }

        public NtfsApiCallResult ReadUsnJournal(SafeFileHandle volumeHandle, ReadUsnJournalRequest request, byte[] outputBuffer, out int bytesReturned)
        {
            bytesReturned = 0;
            return new NtfsApiCallResult(NtfsApiStatus.Success, 0, 0);
        }

        public NtfsApiCallResult EnumerateUsnData(SafeFileHandle volumeHandle, EnumUsnDataRequest request, byte[] outputBuffer, out int bytesReturned)
        {
            enumBuffer.CopyTo(outputBuffer, 0);
            bytesReturned = enumBuffer.Length;
            return new NtfsApiCallResult(NtfsApiStatus.Success, bytesReturned, 0);
        }

        private static byte[] CreateEnumBuffer()
        {
            const long fileId = 0x0001_0000_0000_0001;
            const long parentId = 0x0000_0000_0000_0005;
            const long usn = 7;
            var nameBytes = Encoding.Unicode.GetBytes("stable.txt");
            var recordLength = 64 + nameBytes.Length;
            var buffer = new byte[sizeof(ulong) + recordLength];
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(0, sizeof(ulong)), 0);
            var record = buffer.AsSpan(sizeof(ulong));
            BinaryPrimitives.WriteInt32LittleEndian(record, recordLength);
            BinaryPrimitives.WriteInt16LittleEndian(record[4..], 2);
            BinaryPrimitives.WriteInt64LittleEndian(record[8..], fileId);
            BinaryPrimitives.WriteInt64LittleEndian(record[16..], parentId);
            BinaryPrimitives.WriteInt64LittleEndian(record[24..], usn);
            BinaryPrimitives.WriteUInt32LittleEndian(record[52..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(record[56..], (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(record[58..], 64);
            nameBytes.CopyTo(record[64..]);
            return buffer;
        }
    }

    private sealed class StableNtfsApi : StorageChronicle.Platform.Windows.Ntfs.INtfsApi
    {
        private readonly byte[] enumBuffer = CreateEnumBuffer();
        private readonly Action? onEnumeration;
        private readonly long nextUsn;

        public StableNtfsApi(Action? onEnumeration = null, long nextUsn = 7)
        {
            this.onEnumeration = onEnumeration;
            this.nextUsn = nextUsn;
        }

        public SafeFileHandle OpenVolume(string devicePath) => new(new nint(1), ownsHandle: false);

        public NtfsApiCallResult QueryUsnJournal(SafeFileHandle volumeHandle, out UsnJournalData? data)
        {
            data = new UsnJournalData(1, 1, nextUsn, 1, 100, 4096, 4096, 2, 3);
            return new NtfsApiCallResult(NtfsApiStatus.Success, 56, 0);
        }

        public NtfsApiCallResult ReadUsnJournal(SafeFileHandle volumeHandle, ReadUsnJournalRequest request, byte[] outputBuffer, out int bytesReturned)
        {
            bytesReturned = 0;
            return new NtfsApiCallResult(NtfsApiStatus.Success, 0, 0);
        }

        public NtfsApiCallResult EnumerateUsnData(SafeFileHandle volumeHandle, EnumUsnDataRequest request, byte[] outputBuffer, out int bytesReturned)
        {
            onEnumeration?.Invoke();
            enumBuffer.CopyTo(outputBuffer, 0);
            bytesReturned = enumBuffer.Length;
            return new NtfsApiCallResult(NtfsApiStatus.Success, bytesReturned, 0);
        }

        private static byte[] CreateEnumBuffer()
        {
            const long fileId = 0x0001_0000_0000_0001;
            const long parentId = 0x0000_0000_0000_0005;
            const long usn = 8;
            var nameBytes = Encoding.Unicode.GetBytes("stable.txt");
            var recordLength = 64 + nameBytes.Length;
            var buffer = new byte[sizeof(ulong) + recordLength];
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(0, sizeof(ulong)), 0);
            var record = buffer.AsSpan(sizeof(ulong));
            BinaryPrimitives.WriteInt32LittleEndian(record, recordLength);
            BinaryPrimitives.WriteInt16LittleEndian(record[4..], 2);
            BinaryPrimitives.WriteInt64LittleEndian(record[8..], fileId);
            BinaryPrimitives.WriteInt64LittleEndian(record[16..], parentId);
            BinaryPrimitives.WriteInt64LittleEndian(record[24..], usn);
            BinaryPrimitives.WriteUInt32LittleEndian(record[52..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(record[56..], (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(record[58..], 64);
            nameBytes.CopyTo(record[64..]);
            return buffer;
        }
    }

    private sealed class AccessDeniedMetadataNative : IWindowsFileMetadataNative
    {
        public NativeFileMetadataRecord ReadMetadata(string path, string? parentPath = null) => new(
            FileId.Create("0001000000000001"),
            FileId.Create("0000000000000005"),
            "stable.txt",
            FileKind.File,
            null,
            null,
            null,
            null,
            null,
            null,
            FileAttributes.Normal,
            null,
            true,
            true);

        public SafeFileHandle OpenMetadata(string path, bool directory) => new(new IntPtr(-1), ownsHandle: false);

        public SafeFileHandle OpenDirectory(string path) => new(new IntPtr(-1), ownsHandle: false);
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

        public SafeFileHandle OpenMetadata(string path, bool directory) => new(new IntPtr(-1), ownsHandle: false);

        public SafeFileHandle OpenDirectory(string path) => new(new IntPtr(-1), ownsHandle: false);
    }
}
