using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.State;

/// <summary>Reconstructs file objects and parent/name relationships from canonical events.</summary>
public sealed class StateEngine : IStateStore
{
    private readonly object gate = new();
    private readonly Dictionary<FileId, List<Revision>> revisions = new();
    private readonly HashSet<EventId> applied = new();
    private readonly Dictionary<SourceSequence, EventId> sequences = new();
    private long lastSequence = long.MinValue;

    /// <inheritdoc />
    public ValueTask ApplyAsync(CanonicalEvent value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (applied.Contains(value.EventId)) return ValueTask.CompletedTask;
            var sourceSequence = value.Time.SourceSequence.Value;
            if (sourceSequence < lastSequence) throw new StateOrderException($"Source sequence moved backwards from {lastSequence} to {sourceSequence}.");
            if (sequences.TryGetValue(value.Time.SourceSequence, out var other) && other != value.EventId)
            {
                throw new StateOrderException($"Source sequence {sourceSequence} was reused by {other}.");
            }

            if (value.FileId is { } fileId)
            {
                revisions.TryGetValue(fileId, out var history);
                history ??= revisions[fileId] = new List<Revision>();
                var previous = history.Count == 0 ? null : history[^1];
                var metadata = MergeMetadata(previous?.Metadata, value);
                var exists = value.Operation is not (CanonicalOperation.Delete or CanonicalOperation.Recycle) && (value.Metadata?.Exists ?? previous?.Metadata.Exists ?? true);
                if (value.Operation is CanonicalOperation.Delete or CanonicalOperation.Recycle) exists = false;
                if (value.Operation is CanonicalOperation.Create or CanonicalOperation.DirectoryCreate or CanonicalOperation.Restore) exists = true;
                metadata = metadata with { Exists = exists, InRecycleBin = value.Operation == CanonicalOperation.Recycle || metadata.InRecycleBin };
                if (value.Operation == CanonicalOperation.Restore) metadata = metadata with { InRecycleBin = false, Exists = true };
                history.Add(new Revision(value.EventId, value.Time.RecordedUtc, metadata, exists, value.Quality));
            }

            applied.Add(value.EventId);
            sequences[value.Time.SourceSequence] = value.EventId;
            lastSequence = sourceSequence;
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<FileStateSnapshot> GetSnapshotAsync(DateTimeOffset atUtc, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<FileStateEntry> entries;
        lock (gate)
        {
            entries = revisions.Select(pair => (pair.Key, RevisionAt(pair.Value, atUtc)))
                .Where(value => value.Item2 is not null)
                .Select(value => CreateEntry(value.Key, value.Item2!, atUtc))
                .ToList();
        }

        var known = entries.Where(value => value.Metadata.ParentFileId is null || entries.Any(parent => parent.Metadata.FileId == value.Metadata.ParentFileId)).ToList();
        var unplaced = entries.Except(known).ToList();
        return ValueTask.FromResult(new FileStateSnapshot(atUtc, known, unplaced));
    }

    /// <summary>Returns all relationship versions for diagnostics and projection reconstruction.</summary>
    public IReadOnlyList<DirectoryEntryVersion> GetDirectoryEntryHistory(FileId fileId)
    {
        lock (gate)
        {
            return revisions.TryGetValue(fileId, out var values)
                ? values.Select(value => new DirectoryEntryVersion(value.Metadata.FileId, value.Metadata.ParentFileId, value.Metadata.Name, value.EffectiveFromUtc, null, value.Quality)).ToArray()
                : Array.Empty<DirectoryEntryVersion>();
        }
    }

    private FileStateEntry CreateEntry(FileId id, Revision revision, DateTimeOffset atUtc)
    {
        var path = ReconstructPath(id, atUtc, new HashSet<FileId>());
        return new FileStateEntry(revision.Metadata, path, !revision.Exists, revision.Quality);
    }

    private string? ReconstructPath(FileId id, DateTimeOffset atUtc, HashSet<FileId> visiting)
    {
        if (!visiting.Add(id)) return null;
        if (!revisions.TryGetValue(id, out var history)) return null;
        var revision = RevisionAt(history, atUtc);
        if (revision is null) return null;
        if (revision.Metadata.ParentFileId is not { } parent) return revision.Metadata.Name;
        var parentPath = ReconstructPath(parent, atUtc, visiting);
        return parentPath is null ? null : $"{parentPath}\\{revision.Metadata.Name}";
    }

    private static Revision? RevisionAt(IReadOnlyList<Revision> values, DateTimeOffset atUtc) => values.LastOrDefault(value => value.EffectiveFromUtc <= atUtc);

    private static FileMetadata MergeMetadata(FileMetadata? previous, CanonicalEvent value)
    {
        if (value.Metadata is not null) return value.Metadata;
        if (previous is not null)
        {
            return previous with
            {
                ParentFileId = value.ParentFileId ?? previous.ParentFileId,
                Name = value.Name ?? previous.Name
            };
        }

        return new FileMetadata(value.VolumeId ?? VolumeId.Create("unknown"), value.FileId ?? FileId.Create("unknown"), value.ParentFileId, value.Name ?? "(unknown)", FileKind.Unknown, null, null, null, null, null, null, FileAttributes.Normal, null, null, value.Quality, true, false);
    }

    private sealed record Revision(EventId EventId, DateTimeOffset EffectiveFromUtc, FileMetadata Metadata, bool Exists, EventQuality Quality);
}

/// <summary>Raised when canonical source ordering cannot be applied without corrupting state.</summary>
public sealed class StateOrderException(string message) : InvalidOperationException(message);
