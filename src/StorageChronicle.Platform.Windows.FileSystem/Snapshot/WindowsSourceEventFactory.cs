using System.Collections.Immutable;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Platform.Windows.FileSystem.Snapshot;

internal static class WindowsSourceEventFactory
{
    public static SourceEvent Snapshot(VolumeDescriptor volume, NativeSnapshotEntry entry, long sequence)
    {
        var now = DateTimeOffset.UtcNow;
        var metadata = new FileMetadata(volume.Id, entry.FileId, entry.ParentFileId, entry.Name, entry.Kind, entry.LogicalSize, entry.AllocatedSize, entry.CreatedUtc, entry.LastAccessUtc, entry.LastWriteUtc, entry.FileSystemChangeUtc, entry.Attributes, entry.ReparsePointKind, null, entry.Quality, entry.Exists, false);
        return new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.InitialSnapshot, volume.Id, entry.FileId, entry.ParentFileId, entry.Name, null, entry.Kind == FileKind.Directory ? CanonicalOperation.DirectoryCreate : CanonicalOperation.Create, metadata, new EventTime(now, TimeSpan.Zero, null, now, new SourceSequence(sequence), new MountSequence(sequence)), entry.Quality, null, ProcessAttributionQuality.Unknown, null, null, ImmutableDictionary<string, string>.Empty);
    }

    public static SourceEvent Gap(VolumeId volumeId, string reason, long sequence)
    {
        var now = DateTimeOffset.UtcNow;
        var properties = ImmutableDictionary<string, string>.Empty.Add("reason", reason);
        return new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.InitialSnapshot, volumeId, null, null, null, null, CanonicalOperation.UnverifiedGap, null, new EventTime(now, TimeSpan.Zero, null, now, new SourceSequence(sequence), new MountSequence(sequence)), EventQuality.UnverifiedGap, null, ProcessAttributionQuality.Unknown, null, null, properties);
    }
}

internal sealed record NativeSnapshotEntry(
    FileId FileId,
    FileId? ParentFileId,
    string Name,
    FileKind Kind,
    long? LogicalSize,
    long? AllocatedSize,
    DateTimeOffset? CreatedUtc,
    DateTimeOffset? LastAccessUtc,
    DateTimeOffset? LastWriteUtc,
    DateTimeOffset? FileSystemChangeUtc,
    FileAttributes Attributes,
    string? ReparsePointKind,
    EventQuality Quality,
    bool Exists);
