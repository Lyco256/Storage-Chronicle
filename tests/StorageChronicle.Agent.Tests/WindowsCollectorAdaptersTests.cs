using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Text.Json;
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
        var volume = new VolumeDescriptor(VolumeId.Create("volume"), "NTFS", ["C:\\"], false, false, ProtectedVolumeRoles.Unknown, true, true);
        var api = new BoundaryNtfsApi();
        var snapshot = new BoundarySnapshotReader(api);
        var fixtureRoot = CreateFixtureRoot(out var runId);
        var cursorRoot = Path.Combine(fixtureRoot, "cursors");
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
            DeleteOwnedFixture(fixtureRoot, runId);
        }
    }

    [Fact]
    public async Task ExistingUnmarkedCursorDirectoryIsPreservedAndNotAdopted()
    {
        var fixtureRoot = CreateFixtureRoot(out var runId);
        var cursorRoot = Path.Combine(fixtureRoot, "cursors");
        var foreignFile = Path.Combine(cursorRoot, "notes.txt");
        byte[] original = [0x43, 0x55, 0x52, 0x53, 0x4F, 0x52];
        Directory.CreateDirectory(cursorRoot);
        File.WriteAllBytes(foreignFile, original);
        try
        {
            var volume = new VolumeDescriptor(VolumeId.Create("volume"), "NTFS", ["C:\\"], false, false, ProtectedVolumeRoles.Unknown, true, true);
            var api = new BoundaryNtfsApi();
            var snapshot = new BoundarySnapshotReader(api);
            var collector = new WindowsNtfsVolumeCollector(new SingleVolumeEnumerator(volume), api, cursorRoot, initialSnapshotReader: snapshot);
            await foreach (var _ in collector.CollectAsync(TestContext.Current.CancellationToken)) { }

            Assert.Equal(original, File.ReadAllBytes(foreignFile));
            Assert.False(File.Exists(Path.Combine(cursorRoot, ".usn-cursor-owner.json")));
            Assert.Single(Directory.EnumerateFileSystemEntries(cursorRoot));
        }
        finally
        {
            DeleteOwnedFixture(fixtureRoot, runId);
        }
    }

    private static string CreateFixtureRoot(out string runId)
    {
        runId = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "StorageChronicle.AgentTests", runId);
        Directory.CreateDirectory(root);
        using var marker = new FileStream(Path.Combine(root, ".test-owner.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(marker, new { Schema = "StorageChronicle.TestFixtureOwner.v1", RunId = runId });
        return root;
    }

    private static void DeleteOwnedFixture(string root, string runId)
    {
        var expectedParent = Path.Combine(Path.GetTempPath(), "StorageChronicle.AgentTests");
        var fullRoot = Path.GetFullPath(root);
        if (!string.Equals(Path.GetDirectoryName(fullRoot), expectedParent, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The test fixture cleanup path escaped its dedicated temp parent.");
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(fullRoot, ".test-owner.json")));
        if (marker.RootElement.GetProperty("Schema").GetString() != "StorageChronicle.TestFixtureOwner.v1" || marker.RootElement.GetProperty("RunId").GetString() != runId || Path.GetFileName(fullRoot) != runId)
            throw new IOException("The test fixture ownership marker does not match this test run.");
        Directory.Delete(fullRoot, recursive: true);
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
