using System.Collections.Immutable;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.State;
using Xunit;

namespace StorageChronicle.State.Tests;

public sealed class StateEngineTests
{
    private static readonly VolumeId Volume = VolumeId.Create("volume-1");
    private static readonly FileId Root = FileId.Create("root");
    private static readonly FileId OtherRoot = FileId.Create("other-root");
    private static readonly FileId Child = FileId.Create("child");

    [Fact]
    public async Task CreateRenameMoveDeleteAndRestoreReconstructsPointInTimeState()
    {
        var engine = new StateEngine();
        await Apply(engine, Event(1, CanonicalOperation.DirectoryCreate, Root, null, "root", FileKind.Directory, Utc(1), parentKnown: true));
        await Apply(engine, Event(2, CanonicalOperation.Create, Child, Root, "one.txt", FileKind.File, Utc(2)));
        await Apply(engine, Event(3, CanonicalOperation.Rename, Child, Root, "two.txt", FileKind.File, Utc(3)));
        await Apply(engine, Event(4, CanonicalOperation.Delete, Child, Root, "two.txt", FileKind.File, Utc(4)));
        await Apply(engine, Event(5, CanonicalOperation.Restore, Child, Root, "restored.txt", FileKind.File, Utc(5)));

        var beforeRename = await SnapshotAt(engine, 2);
        var afterRename = await SnapshotAt(engine, 3);
        var afterDelete = await SnapshotAt(engine, 4);
        var afterRestore = await SnapshotAt(engine, 5);

        Assert.Contains(beforeRename.Entries, item => item.ReconstructedPath == "root\\one.txt");
        Assert.Contains(afterRename.Entries, item => item.ReconstructedPath == "root\\two.txt");
        Assert.DoesNotContain(afterDelete.Entries, item => item.Metadata.FileId == Child);
        Assert.Contains(afterRestore.Entries, item => item.ReconstructedPath == "root\\restored.txt");
    }

    [Fact]
    public async Task FolderMoveChangesOneEntryAndReconstructsOldAndNewPathsIncludingLaterChild()
    {
        var engine = new StateEngine();
        await Apply(engine, Event(1, CanonicalOperation.DirectoryCreate, Root, null, "A", FileKind.Directory, Utc(1), parentKnown: true));
        await Apply(engine, Event(2, CanonicalOperation.DirectoryCreate, OtherRoot, null, "B", FileKind.Directory, Utc(2), parentKnown: true));
        await Apply(engine, Event(3, CanonicalOperation.DirectoryCreate, Child, Root, "old-child", FileKind.Directory, Utc(3)));
        await Apply(engine, Event(4, CanonicalOperation.Move, Root, OtherRoot, "A", FileKind.Directory, Utc(4)));
        var versions = engine.GetDirectoryEntryHistory(Root);
        await Apply(engine, Event(5, CanonicalOperation.Create, FileId.Create("new-child"), Root, "new-child", FileKind.File, Utc(5)));

        var before = await SnapshotAt(engine, 3);
        var after = await SnapshotAt(engine, 4);
        var afterAdd = await SnapshotAt(engine, 5);

        Assert.Equal(2, versions?.Versions.Length);
        Assert.Single(engine.GetDirectoryEntryHistory(Child)!.Versions);
        Assert.Contains(before.Entries, item => item.ReconstructedPath == "A\\old-child");
        Assert.Contains(after.Entries, item => item.ReconstructedPath == "B\\A\\old-child");
        Assert.Contains(afterAdd.Entries, item => item.ReconstructedPath == "B\\A\\new-child");
    }

    [Fact]
    public async Task ChildMovedAwayAfterFolderMoveStopsBeingADeletedFolderDescendant()
    {
        var engine = new StateEngine();
        var child = FileId.Create("moving-child");
        await Apply(engine, Event(1, CanonicalOperation.DirectoryCreate, Root, null, "folder", FileKind.Directory, Utc(1), parentKnown: true));
        await Apply(engine, Event(2, CanonicalOperation.DirectoryCreate, OtherRoot, null, "elsewhere", FileKind.Directory, Utc(2), parentKnown: true));
        await Apply(engine, Event(3, CanonicalOperation.Create, child, Root, "child", FileKind.File, Utc(3)));
        await Apply(engine, Event(4, CanonicalOperation.Delete, Root, null, "folder", FileKind.Directory, Utc(4)));
        await Apply(engine, Event(5, CanonicalOperation.Move, child, OtherRoot, "child", FileKind.File, Utc(5)));

        var afterDelete = await SnapshotAt(engine, 4);
        var afterMove = await SnapshotAt(engine, 5);

        Assert.Contains(afterDelete.Entries, item => item.ReconstructedPath == "folder" && item.IsVirtualDeleted);
        Assert.Contains(afterDelete.Entries, item => item.ReconstructedPath == "folder\\child" && item.IsVirtualDeleted);
        Assert.Contains(afterMove.Entries, item => item.ReconstructedPath == "elsewhere\\child");
        Assert.DoesNotContain(afterMove.Entries, item => item.Metadata.FileId == child && item.IsVirtualDeleted);
    }

    [Fact]
    public async Task SamePathDifferentIdsRemainSeparateAndObjectHistoryDoesNotCreateEntryForDataWrite()
    {
        var engine = new StateEngine();
        var first = FileId.Create("first");
        var second = FileId.Create("second");
        await Apply(engine, Event(1, CanonicalOperation.Create, first, null, "same.txt", FileKind.File, Utc(1), parentKnown: true));
        await Apply(engine, Event(2, CanonicalOperation.Create, second, null, "same.txt", FileKind.File, Utc(2), parentKnown: true));
        await Apply(engine, Event(3, CanonicalOperation.DataWrite, first, null, null, FileKind.File, Utc(3), logicalSize: 42));

        var snapshot = await SnapshotAt(engine, 3);

        Assert.Equal(2, snapshot.Entries.Count(item => item.ReconstructedPath == "same.txt"));
        Assert.Single(engine.GetDirectoryEntryHistory(first)!.Versions);
        Assert.Equal(2, engine.GetFileObject(first)!.Versions.Length);
    }

    [Fact]
    public async Task ParentKnownNullMovesAnEntryToTheRoot()
    {
        var engine = new StateEngine();
        await Apply(engine, Event(1, CanonicalOperation.DirectoryCreate, Root, null, "root", FileKind.Directory, Utc(1), parentKnown: true));
        await Apply(engine, Event(2, CanonicalOperation.Create, Child, Root, "child", FileKind.File, Utc(2)));
        await Apply(engine, Event(3, CanonicalOperation.Move, Child, null, "child", FileKind.File, Utc(3), parentKnown: true));

        var snapshot = await SnapshotAt(engine, 3);

        Assert.Contains(snapshot.Entries, item => item.Metadata.FileId == Child && item.ReconstructedPath == "child");
    }

    [Fact]
    public async Task ExistenceOnlyAndUnknownParentAreKeptAsIncompleteUnplacedState()
    {
        var engine = new StateEngine();
        var file = FileId.Create("existence-only");
        await Apply(engine, Event(1, CanonicalOperation.Create, file, null, "unknown.txt", FileKind.Unknown, Utc(1), EventQuality.ExistenceOnly, parentUnknown: true, metadata: false));

        var snapshot = await SnapshotAt(engine, 1);

        var entry = Assert.Single(snapshot.UnplacedEntries);
        Assert.Equal(file, entry.Metadata.FileId);
        Assert.Equal(EventQuality.ExistenceOnly, entry.Quality);
        Assert.Null(entry.ReconstructedPath);
    }

    [Fact]
    public async Task DuplicateIsIdempotentAndGapOrReverseLeavesStateReadyForRecovery()
    {
        var engine = new StateEngine();
        var first = Event(1, CanonicalOperation.Create, Child, null, "child", FileKind.File, Utc(1), parentKnown: true);
        await Apply(engine, first);
        await Apply(engine, first);

        await Assert.ThrowsAsync<SourceSequenceGapException>(async () => await Apply(engine, Event(3, CanonicalOperation.DataWrite, Child, null, null, FileKind.File, Utc(3))));
        await Apply(engine, Event(2, CanonicalOperation.DataWrite, Child, null, null, FileKind.File, Utc(2)));
        await Assert.ThrowsAsync<SourceSequenceOrderException>(async () => await Apply(engine, Event(1, CanonicalOperation.DataWrite, Child, null, null, FileKind.File, Utc(4))));
        await Apply(engine, Event(3, CanonicalOperation.DataWrite, Child, null, null, FileKind.File, Utc(3)));

        Assert.Equal(3, engine.GetAppliedEvents().Length);
        Assert.Single((await SnapshotAt(engine, 3)).Entries);
    }

    [Fact]
    public async Task ClockRegressionIsClampedToMonotonicEffectiveTime()
    {
        var engine = new StateEngine();
        await Apply(engine, Event(1, CanonicalOperation.Create, Child, null, "old.txt", FileKind.File, Utc(10), parentKnown: true));
        await Apply(engine, Event(2, CanonicalOperation.Rename, Child, null, "new.txt", FileKind.File, Utc(9)));

        var applied = engine.GetAppliedEvents();
        var snapshot = await Snapshot(engine, Utc(10));

        Assert.Equal(applied[0].EffectiveUtc, applied[1].EffectiveUtc);
        Assert.Contains(snapshot.Entries, item => item.ReconstructedPath == "new.txt");
    }

    [Fact]
    public async Task ReconciliationIsRecordedSeparatelyFromObservation()
    {
        var engine = new StateEngine();
        await Apply(engine, Event(1, CanonicalOperation.Create, Child, null, "child", FileKind.File, Utc(1), parentKnown: true));
        await Apply(engine, Event(1, CanonicalOperation.ReconciliationDiscovered, Child, null, "child", FileKind.File, Utc(2), EventQuality.Reconciled, origin: EventOrigin.MftReconciliation));

        var applied = engine.GetAppliedEvents();

        Assert.Equal(StateEventKind.Observation, applied[0].Kind);
        Assert.Equal(StateEventKind.Reconciliation, applied[1].Kind);
        Assert.Equal(EventQuality.Reconciled, (await SnapshotAt(engine, 2)).Entries.Single().Quality);
    }

    [Fact]
    public async Task CancellationAndCycleCorruptionDoNotMutateState()
    {
        var engine = new StateEngine();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await engine.ApplyAsync(Event(1, CanonicalOperation.Create, Child, null, "child", FileKind.File, Utc(1)), cancellation.Token));

        await Apply(engine, Event(1, CanonicalOperation.DirectoryCreate, Root, null, "root", FileKind.Directory, Utc(1), parentKnown: true));
        await Apply(engine, Event(2, CanonicalOperation.DirectoryCreate, Child, Root, "child", FileKind.Directory, Utc(2)));
        await Assert.ThrowsAsync<StateCorruptionException>(async () => await Apply(engine, Event(3, CanonicalOperation.Move, Root, Child, "root", FileKind.Directory, Utc(3))));

        Assert.Equal(2, engine.GetAppliedEvents().Length);
    }

    [Fact]
    public async Task CanceledSnapshotDoesNotExposePartialReadModel()
    {
        var engine = new StateEngine();
        await Apply(engine, Event(1, CanonicalOperation.Create, Child, null, "child", FileKind.File, Utc(1), parentKnown: true));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await engine.GetSnapshotAsync(Utc(1), cancellation.Token));
        Assert.Single((await SnapshotAt(engine, 1)).Entries);
    }

    private static DateTimeOffset Utc(int second) => new(2026, 1, 1, 0, 0, second, TimeSpan.Zero);

    private static CanonicalEvent Event(
        long sequence,
        CanonicalOperation operation,
        FileId? fileId,
        FileId? parent,
        string? name,
        FileKind kind,
        DateTimeOffset time,
        EventQuality quality = EventQuality.Exact,
        EventOrigin origin = EventOrigin.LiveUsn,
        bool parentKnown = false,
        bool parentUnknown = false,
        bool metadata = true,
        long? logicalSize = null)
    {
        var properties = ImmutableDictionary<string, string>.Empty;
        if (parentKnown)
        {
            properties = properties.Add("parentKnown", "true");
        }

        if (parentUnknown)
        {
            properties = properties.Add("parentUnknown", "true");
        }

        var fileMetadata = metadata && fileId is { } id
            ? new FileMetadata(Volume, id, parent, name ?? string.Empty, kind, logicalSize, logicalSize, null, null, time, time, 0, null, null, quality, true, false)
            : null;
        return new CanonicalEvent(EventId.New(), EventSchemaVersion.Current, operation, origin, Volume, fileId, parent, name, null, fileMetadata, new EventTime(time, TimeSpan.Zero, null, time, new SourceSequence(sequence), new MountSequence(sequence)), quality, null, ProcessAttributionQuality.Unknown, null, null, properties);
    }

    private static ValueTask Apply(StateEngine engine, CanonicalEvent value) => engine.ApplyAsync(value, TestContext.Current.CancellationToken);

    private static ValueTask<FileStateSnapshot> Snapshot(StateEngine engine, DateTimeOffset atUtc) => engine.GetSnapshotAsync(atUtc, TestContext.Current.CancellationToken);

    private static ValueTask<FileStateSnapshot> SnapshotAt(StateEngine engine, long stateSequence) => engine.GetSnapshotAtSequenceAsync(stateSequence, TestContext.Current.CancellationToken);
}
