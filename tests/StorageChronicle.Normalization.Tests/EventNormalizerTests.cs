using System.Collections.Immutable;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Normalization;
using Xunit;

namespace StorageChronicle.Normalization.Tests;

public sealed class EventNormalizerTests
{
    private static readonly VolumeId Volume = VolumeId.Create("volume-a");
    private static readonly MountSessionId Mount = MountSessionId.Create("mount-a");
    private static readonly ProcessInstanceId Explorer = ProcessInstanceId.Create("explorer-instance");
    private static readonly DateTimeOffset BaseTime = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(CanonicalOperation.Create, CanonicalOperation.Create)]
    [InlineData(CanonicalOperation.DataWrite, CanonicalOperation.DataWrite)]
    [InlineData(CanonicalOperation.Extend, CanonicalOperation.Extend)]
    [InlineData(CanonicalOperation.Truncate, CanonicalOperation.Truncate)]
    [InlineData(CanonicalOperation.Delete, CanonicalOperation.Delete)]
    [InlineData(CanonicalOperation.SecurityMetadataChanged, CanonicalOperation.SecurityMetadataChanged)]
    public void HintedOperationIsMappedDeterministically(CanonicalOperation hint, CanonicalOperation expected)
    {
        var source = Source(hint: hint);

        var result = new EventNormalizer().Normalize(source);

        Assert.NotNull(result);
        Assert.Equal(expected, result.Operation);
        Assert.Equal(source.EventId, result.EventId);
    }

    [Fact]
    public void DirectoryCreateIsSelectedFromMetadataWithoutSyntheticDescendants()
    {
        var source = Source(
            fileId: "directory",
            name: "Folder",
            hint: CanonicalOperation.Create,
            metadata: Metadata("directory", "Folder", FileKind.Directory));

        var result = new EventNormalizer().Normalize(source);

        Assert.NotNull(result);
        Assert.Equal(CanonicalOperation.DirectoryCreate, result.Operation);
        Assert.Equal("directory", result.FileId?.Value);
    }

    [Fact]
    public void RenameAndMoveRetainIdentityAndRelationshipFacts()
    {
        var rename = Source(
            name: "new.txt",
            oldName: "old.txt",
            hint: CanonicalOperation.Rename);
        var move = Source(
            name: "new.txt",
            oldName: "old.txt",
            parentFileId: "new-parent",
            hint: CanonicalOperation.Rename,
            properties: Properties(("oldParentFileId", "old-parent")));

        var normalizer = new EventNormalizer();
        var renamed = normalizer.Normalize(rename);
        var moved = normalizer.Normalize(move);

        Assert.NotNull(renamed);
        Assert.Equal(CanonicalOperation.Rename, renamed.Operation);
        Assert.Equal("old.txt", renamed.OldName);
        Assert.NotNull(moved);
        Assert.Equal(CanonicalOperation.Move, moved.Operation);
        Assert.Equal("old-parent", moved.Properties["oldparentfileid"]);
    }

    [Fact]
    public void RecycleAndRestoreRemainDistinct()
    {
        var recycle = Source(properties: Properties(("recycleAction", "Recycled")));
        var restore = Source(properties: Properties(("recycleAction", "Restored")));
        var normalizer = new EventNormalizer();

        Assert.Equal(CanonicalOperation.Recycle, normalizer.Normalize(recycle)?.Operation);
        Assert.Equal(CanonicalOperation.Restore, normalizer.Normalize(restore)?.Operation);
    }

    [Fact]
    public void ShareCloudAndReconciliationOriginsDoNotCollapseIntoMetadataChange()
    {
        var normalizer = new EventNormalizer();

        var share = normalizer.Normalize(Source(origin: EventOrigin.ShareChange, hint: null));
        var cloud = normalizer.Normalize(Source(properties: Properties(("cloudState", "Online")), hint: CanonicalOperation.MetadataChanged));
        var reconciled = normalizer.Normalize(Source(origin: EventOrigin.MftReconciliation, hint: CanonicalOperation.Delete));

        Assert.Equal(CanonicalOperation.ShareChanged, share?.Operation);
        Assert.Equal(CanonicalOperation.CloudStateChanged, cloud?.Operation);
        Assert.Equal(CanonicalOperation.ReconciliationDiscovered, reconciled?.Operation);
        Assert.Equal(EventQuality.Reconciled, reconciled?.Quality);
    }

    [Fact]
    public void ReadOnlyEtwObservationIsNotConvertedToDurableEvent()
    {
        var source = Source(
            origin: EventOrigin.Etw,
            hint: null,
            properties: Properties(("observation", "DirectoryEnumeration")));

        var result = new EventNormalizer().Normalize(source);

        Assert.Null(result);
    }

    [Fact]
    public void ClipboardCopyCorrelatesOnlyAValidExplorerCreate()
    {
        var normalizer = new EventNormalizer();
        var clipboard = Source(
            eventId: NewId(),
            fileId: "source-file",
            parentFileId: "source-parent",
            properties: Properties(("clipboardGeneration", "generation-1"), ("clipboardIntent", "Copy")),
            origin: EventOrigin.Clipboard);
        var create = Source(
            eventId: NewId(),
            fileId: "destination-file",
            properties: Properties(("clipboardGeneration", "generation-1"), ("explorerOperation", "Paste")),
            hint: CanonicalOperation.Create,
            processInstanceId: Explorer);

        Assert.Null(normalizer.Normalize(clipboard));
        var result = normalizer.Normalize(create);

        Assert.NotNull(result);
        Assert.Equal(CanonicalOperation.Create, result.Operation);
        Assert.True(result.Properties.ContainsKey("correlationquality"), string.Join(",", result.Properties.Select(pair => $"{pair.Key}={pair.Value}")));
        Assert.Equal("source-file", result.Properties["copysourcefileid"]);
        Assert.Equal("Correlated", result.Properties["copycorrelationquality"]);
    }

    [Fact]
    public void ClipboardDirectoryCopyCorrelatesTheRootOnly()
    {
        var normalizer = new EventNormalizer();
        var clipboard = Source(
            eventId: NewId(),
            fileId: "source-folder",
            metadata: Metadata("source-folder", "Folder", FileKind.Directory),
            properties: Properties(("clipboardGeneration", "generation-folder"), ("clipboardIntent", "Copy")),
            origin: EventOrigin.Clipboard);
        var root = Source(
            eventId: NewId(),
            fileId: "destination-folder",
            metadata: Metadata("destination-folder", "Folder", FileKind.Directory),
            properties: Properties(("clipboardGeneration", "generation-folder"), ("explorerOperation", "Create")),
            hint: CanonicalOperation.Create,
            processInstanceId: Explorer);

        normalizer.Normalize(clipboard);
        var result = normalizer.Normalize(root);

        Assert.NotNull(result);
        Assert.Equal(CanonicalOperation.DirectoryCreate, result.Operation);
        Assert.Equal("DirectoryRoot", result.Properties["copyscope"]);
    }

    [Fact]
    public void SameVolumeCutWithConfirmedIdentityBecomesMove()
    {
        var normalizer = new EventNormalizer();
        var clipboard = Source(
            eventId: NewId(),
            fileId: "cut-file",
            parentFileId: "old-parent",
            properties: Properties(("clipboardGeneration", "generation-cut"), ("clipboardIntent", "Cut")),
            origin: EventOrigin.Clipboard);
        var target = Source(
            eventId: NewId(),
            fileId: "cut-file",
            parentFileId: "new-parent",
            properties: Properties(("clipboardGeneration", "generation-cut"), ("explorerOperation", "Paste")),
            hint: CanonicalOperation.Create,
            processInstanceId: Explorer);

        normalizer.Normalize(clipboard);
        var result = normalizer.Normalize(target);

        Assert.NotNull(result);
        Assert.Equal(CanonicalOperation.Move, result.Operation);
        Assert.Equal("Correlated", result.Properties["cutcorrelationquality"]);
    }

    [Fact]
    public void CrossVolumeCutIsNotMisclassifiedAsMove()
    {
        var normalizer = new EventNormalizer();
        var clipboard = Source(
            eventId: NewId(),
            fileId: "cut-file",
            parentFileId: "old-parent",
            properties: Properties(("clipboardGeneration", "generation-cross-volume"), ("clipboardIntent", "Cut")),
            origin: EventOrigin.Clipboard,
            volumeId: Volume);
        var target = Source(
            eventId: NewId(),
            fileId: "cut-file",
            parentFileId: "new-parent",
            properties: Properties(("clipboardGeneration", "generation-cross-volume"), ("explorerOperation", "Paste")),
            hint: CanonicalOperation.Create,
            processInstanceId: Explorer,
            volumeId: VolumeId.Create("volume-b"));

        normalizer.Normalize(clipboard);
        var result = normalizer.Normalize(target);

        Assert.NotNull(result);
        Assert.Equal(CanonicalOperation.Create, result.Operation);
        Assert.Equal("IdentityOrVolumeNotConfirmed", result.Properties["cutcorrelationrejected"]);
    }

    [Fact]
    public void MissingUsnMetadataIsKeptWithJournalOnlyQuality()
    {
        var source = Source(hint: CanonicalOperation.Delete, metadata: null, origin: EventOrigin.LiveUsn, quality: EventQuality.Exact);

        var result = new EventNormalizer().Normalize(source);

        Assert.NotNull(result);
        Assert.Equal(EventQuality.JournalOnly, result.Quality);
    }

    [Fact]
    public void MissingOperationIsRetainedAsUnverifiedGap()
    {
        var result = new EventNormalizer().Normalize(Source(hint: null, properties: ImmutableDictionary<string, string>.Empty));

        Assert.NotNull(result);
        Assert.Equal(CanonicalOperation.UnverifiedGap, result.Operation);
        Assert.Equal(EventQuality.UnverifiedGap, result.Quality);
    }

    [Fact]
    public void DuplicateSourceEventIsIdempotentAndConflictingFactsFail()
    {
        var source = Source(hint: CanonicalOperation.DataWrite);
        var normalizer = new EventNormalizer();

        var first = normalizer.Normalize(source);
        var second = normalizer.Normalize(source);
        var conflicting = source with { Name = "different-name" };

        Assert.Equal(first, second);
        Assert.Throws<NormalizationException>(() => normalizer.Normalize(conflicting));
    }

    [Fact]
    public async Task CancellationDoesNotCreateRememberedState()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = Source(hint: CanonicalOperation.Create);
        var normalizer = new EventNormalizer();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await normalizer.NormalizeAsync(source, cancellation.Token));
        Assert.NotNull(normalizer.Normalize(source));
    }

    [Fact]
    public void FileContentAndHashPropertiesAreNeverCarriedForward()
    {
        var source = Source(
            hint: CanonicalOperation.DataWrite,
            properties: Properties(("fileContent", "secret"), ("sha256", "should-not-exist"), ("operation", "DataWrite")));

        var result = new EventNormalizer().Normalize(source);

        Assert.NotNull(result);
        Assert.DoesNotContain(result.Properties.Keys, key => key.Contains("content", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Properties.Keys, key => key.Contains("hash", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("DataWrite", result.Properties["operation"]);
    }

    [Fact]
    public void CorrelatedProcessQualityIsNotUpgraded()
    {
        var source = Source(hint: CanonicalOperation.Create, processQuality: ProcessAttributionQuality.Correlated);

        var result = new EventNormalizer().Normalize(source);

        Assert.NotNull(result);
        Assert.Equal(ProcessAttributionQuality.Correlated, result.ProcessQuality);
    }

    [Fact]
    public void InvalidBoundsFailBeforeProcessingAnyEvent()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventNormalizer(new NormalizationOptions { MaximumRememberedEvents = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventNormalizer(new NormalizationOptions { MaximumClipboardCandidates = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventNormalizer(new NormalizationOptions { ClipboardValidity = TimeSpan.Zero }));
    }

    private static SourceEvent Source(
        CanonicalOperation? hint = CanonicalOperation.Create,
        EventOrigin origin = EventOrigin.LiveUsn,
        EventQuality quality = EventQuality.Exact,
        string? fileId = "file",
        string? parentFileId = "parent",
        string? name = "file.txt",
        string? oldName = null,
        VolumeId? volumeId = null,
        FileMetadata? metadata = null,
        ProcessInstanceId? processInstanceId = null,
        ProcessAttributionQuality processQuality = ProcessAttributionQuality.Unknown,
        ImmutableDictionary<string, string>? properties = null,
        EventId? eventId = null)
    {
        var actualVolume = volumeId ?? Volume;
        FileId? actualFileId = fileId is null ? null : FileId.Create(fileId);
        FileId? actualParent = parentFileId is null ? null : FileId.Create(parentFileId);
        return new SourceEvent(
            eventId ?? NewId(),
            EventSchemaVersion.Current,
            origin,
            actualVolume,
            actualFileId,
            actualParent,
            name,
            oldName,
            hint,
            metadata,
            EventTime(BaseTime),
            quality,
            processInstanceId,
            processQuality,
            Mount,
            null,
            properties ?? ImmutableDictionary<string, string>.Empty);
    }

    private static FileMetadata Metadata(string fileId, string name, FileKind kind, bool inRecycleBin = false) =>
        new(Volume, FileId.Create(fileId), FileId.Create("parent"), name, kind, null, null, null, null, null, null, FileAttributes.None, null, null, EventQuality.Exact, true, inRecycleBin);

    private static EventTime EventTime(DateTimeOffset at) =>
        new(at, TimeSpan.Zero, at, at, new SourceSequence(1), new MountSequence(1));

    private static ImmutableDictionary<string, string> Properties(params (string Key, string Value)[] values) =>
        values.ToImmutableDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase);

    private static EventId NewId() => new(Guid.NewGuid());
}
