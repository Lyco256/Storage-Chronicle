using System.Collections.Immutable;
using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using StorageChronicle.Agent;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Abstractions;
using StorageChronicle.Platform.Windows.Ntfs;
using Xunit;

namespace StorageChronicle.Agent.Tests;

public sealed class WindowsCollectorAdaptersTests
{
    [Fact]
    public async Task InitialNtfsScanCapturesBoundaryBeforeMetadataSnapshotAndRecoversFromIt()
    {
        var volume = new VolumeDescriptor(VolumeId.Create("volume"), "NTFS", ["C:\\"], false, false, false, true, true);
        var api = new BoundaryNtfsApi();
        var snapshot = new BoundarySnapshotReader(api);
        var cursorRoot = Path.Combine(Path.GetTempPath(), "StorageChronicle.AgentTests", Guid.NewGuid().ToString("N"));
        try
        {
            var collector = new WindowsNtfsVolumeCollector(new SingleVolumeEnumerator(volume), api, cursorRoot, initialSnapshotReader: snapshot);
            var results = new List<SourceEvent>();
            await foreach (var value in collector.CollectAsync(TestContext.Current.CancellationToken)) results.Add(value);

            Assert.NotEmpty(results);
            Assert.True(api.BoundaryWasQueried);
            Assert.Contains(results, value => value.Origin == EventOrigin.InitialSnapshot && value.Metadata?.LogicalSize == 42);
            Assert.Equal(100, api.FirstBoundaryNextUsn);
        }
        finally
        {
            if (Directory.Exists(cursorRoot)) Directory.Delete(cursorRoot, recursive: true);
        }
    }

    private sealed class SingleVolumeEnumerator(VolumeDescriptor volume) : IVolumeEnumerator
    {
        public ValueTask<IReadOnlyList<VolumeDescriptor>> EnumerateAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<VolumeDescriptor>>([volume]);
    }

    private sealed class BoundarySnapshotReader(BoundaryNtfsApi api) : IVolumeSnapshotReader
    {
        public async IAsyncEnumerable<SourceEvent> ReadInitialSnapshotAsync(VolumeDescriptor volume, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Assert.True(api.BoundaryWasQueried, "The metadata snapshot must start after the pre-scan journal boundary.");
            await Task.Yield();
            var now = DateTimeOffset.UtcNow;
            var fileId = FileId.Create("file");
            var metadata = new FileMetadata(volume.Id, fileId, null, "file.txt", FileKind.File, 42, null, now, now, now, now, FileAttributes.Normal, null, null, EventQuality.Exact, true, false);
            yield return new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.InitialSnapshot, volume.Id, fileId, null, metadata.Name, null, CanonicalOperation.Create, metadata,
                new EventTime(now, now.Offset, null, now, new SourceSequence(1), new MountSequence(1)), EventQuality.Exact, null, ProcessAttributionQuality.Unknown, null, null, ImmutableDictionary<string, string>.Empty);
        }
    }

    private sealed class BoundaryNtfsApi : INtfsApi
    {
        public bool BoundaryWasQueried { get; private set; }
        public long FirstBoundaryNextUsn { get; private set; }
        private int queryCount;

        public SafeFileHandle OpenVolume(string devicePath) => new(new nint(1), ownsHandle: false);

        public NtfsApiCallResult QueryUsnJournal(SafeFileHandle volumeHandle, out UsnJournalData? data)
        {
            queryCount++;
            data = new UsnJournalData(1, 1, queryCount == 1 ? 100 : 101, 1, 1_000, 1_024, 512, 2, 3);
            if (queryCount == 1) FirstBoundaryNextUsn = data.NextUsn;
            BoundaryWasQueried = true;
            return new NtfsApiCallResult(NtfsApiStatus.Success, 56, 0);
        }

        public NtfsApiCallResult ReadUsnJournal(SafeFileHandle volumeHandle, ReadUsnJournalRequest request, byte[] outputBuffer, out int bytesReturned)
        {
            bytesReturned = 0;
            return new NtfsApiCallResult(NtfsApiStatus.Success, 0, 0);
        }

        public NtfsApiCallResult EnumerateUsnData(SafeFileHandle volumeHandle, EnumUsnDataRequest request, byte[] outputBuffer, out int bytesReturned)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(outputBuffer.AsSpan(0, sizeof(ulong)), request.StartFileReferenceNumber + 1);
            bytesReturned = sizeof(ulong);
            return new NtfsApiCallResult(NtfsApiStatus.Success, bytesReturned, 0);
        }
    }
}
