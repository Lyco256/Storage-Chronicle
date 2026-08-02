using System.Collections.Immutable;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.State;
using Xunit;

namespace StorageChronicle.State.Tests;

public sealed class StateEngineTests
{
    [Fact]
    public async Task FolderMoveReconstructsOldAndNewPathsWithoutDescendantEvents()
    {
        var state = new StateEngine();
        var volume = VolumeId.Create("v");
        var root = Metadata(volume, "root", null, FileKind.Directory);
        var child = Metadata(volume, "child.txt", FileId.Create("root"), FileKind.File);
        var destination = Metadata(volume, "dest", null, FileKind.Directory);
        await state.ApplyAsync(Event(destination, CanonicalOperation.DirectoryCreate, 0, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        await state.ApplyAsync(Event(root, CanonicalOperation.DirectoryCreate, 1, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        await state.ApplyAsync(Event(child, CanonicalOperation.Create, 2, new DateTimeOffset(2026, 1, 1, 0, 1, 0, TimeSpan.Zero)));
        await state.ApplyAsync(Event(root with { ParentFileId = FileId.Create("dest"), Name = "moved" }, CanonicalOperation.Move, 3, new DateTimeOffset(2026, 1, 1, 0, 2, 0, TimeSpan.Zero)));
        var now = await state.GetSnapshotAsync(new DateTimeOffset(2026, 1, 1, 0, 3, 0, TimeSpan.Zero));
        Assert.Contains(now.Entries, value => value.Metadata.FileId == child.FileId && value.ReconstructedPath == "dest\\moved\\child.txt");
    }

    [Fact]
    public async Task DuplicateEventIsIdempotentAndBackwardsSequenceIsRejected()
    {
        var state = new StateEngine();
        var value = Event(Metadata(VolumeId.Create("v"), "f", null, FileKind.File), CanonicalOperation.Create, 4, DateTimeOffset.UtcNow);
        await state.ApplyAsync(value);
        await state.ApplyAsync(value);
        await Assert.ThrowsAsync<StateOrderException>(async () => await state.ApplyAsync(value with { EventId = EventId.New(), Time = value.Time with { SourceSequence = new SourceSequence(3) } }));
    }

    [Fact]
    public async Task UnknownParentIsUnplacedAndDeletionRemainsVirtual()
    {
        var state = new StateEngine();
        var metadata = Metadata(VolumeId.Create("v"), "orphan", FileId.Create("missing"), FileKind.File);
        await state.ApplyAsync(Event(metadata, CanonicalOperation.Create, 1, DateTimeOffset.UtcNow));
        var snapshot = await state.GetSnapshotAsync(DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Single(snapshot.UnplacedEntries);
        await state.ApplyAsync(Event(metadata, CanonicalOperation.Delete, 2, DateTimeOffset.UtcNow.AddSeconds(2)));
        snapshot = await state.GetSnapshotAsync(DateTimeOffset.UtcNow.AddSeconds(3));
        Assert.True(snapshot.UnplacedEntries.Single().IsVirtualDeleted);
    }

    private static FileMetadata Metadata(VolumeId volume, string name, FileId? parent, FileKind kind) => new(volume, FileId.Create(name == "root" ? "root" : name), parent, name, kind, 1, 1, null, null, null, null, FileAttributes.Normal, null, null, EventQuality.Exact, true, false);
    private static CanonicalEvent Event(FileMetadata metadata, CanonicalOperation operation, long sequence, DateTimeOffset at) => new(EventId.New(), EventSchemaVersion.Current, operation, EventOrigin.LiveUsn, metadata.VolumeId, metadata.FileId, metadata.ParentFileId, metadata.Name, null, metadata, new EventTime(at, TimeSpan.Zero, null, at, new SourceSequence(sequence), new MountSequence(sequence)), metadata.Quality, null, ProcessAttributionQuality.Unknown, null, null, ImmutableDictionary<string, string>.Empty);
}
