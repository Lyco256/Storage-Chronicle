using System.Collections.Immutable;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.State;

/// <summary>Separates durable file-object facts from versioned directory relationships.</summary>
public sealed record FileObjectVersion(
    FileId FileId,
    FileObjectMetadata Metadata,
    DateTimeOffset EffectiveFromUtc,
    DateTimeOffset? EffectiveToUtc,
    EventQuality Quality,
    bool Exists,
    bool InRecycleBin,
    long StateSequence);

/// <summary>Metadata that belongs to a file object and not to one directory entry.</summary>
public sealed record FileObjectMetadata(
    VolumeId VolumeId,
    FileId FileId,
    FileKind Kind,
    long? LogicalSize,
    long? AllocatedSize,
    DateTimeOffset? CreatedUtc,
    DateTimeOffset? LastAccessUtc,
    DateTimeOffset? LastWriteUtc,
    DateTimeOffset? FileSystemChangeUtc,
    FileAttributes Attributes,
    string? ReparsePointKind,
    string? CloudPlaceholderState);

/// <summary>Immutable read model containing all known versions of one file object.</summary>
public sealed record FileObject(FileId FileId, ImmutableArray<FileObjectVersion> Versions);

/// <summary>Immutable read model containing the observed directory relationship history of one file ID.</summary>
public sealed record DirectoryEntryHistory(FileId FileId, ImmutableArray<DirectoryEntryVersion> Versions);

/// <summary>Identifies whether an applied event was an observation or a reconciliation result.</summary>
public enum StateEventKind
{
    /// <summary>Normal source or normalized observation.</summary>
    Observation,
    /// <summary>State produced by a reconciliation source.</summary>
    Reconciliation,
    /// <summary>A continuity gap marker.</summary>
    Gap
}

/// <summary>Describes one event accepted by the state engine.</summary>
public sealed record AppliedStateEvent(
    EventId EventId,
    CanonicalOperation Operation,
    StateEventKind Kind,
    DateTimeOffset EffectiveUtc,
    SourceSequence SourceSequence,
    long StateSequence);

/// <summary>Provides state reconstruction without a dependency on UI, Windows, or storage implementations.</summary>
public interface IStateEngine
{
    /// <summary>Applies one canonical event exactly once.</summary>
    ValueTask ApplyAsync(CanonicalEvent value, CancellationToken cancellationToken = default);

    /// <summary>Returns a read-only snapshot at a wall-clock instant.</summary>
    ValueTask<FileStateSnapshot> GetSnapshotAsync(DateTimeOffset atUtc, CancellationToken cancellationToken = default);

    /// <summary>Returns a read-only snapshot after the specified state-application sequence.</summary>
    ValueTask<FileStateSnapshot> GetSnapshotAtSequenceAsync(long stateSequence, CancellationToken cancellationToken = default);

    /// <summary>Returns the immutable object history for a file ID, or null when it is unknown.</summary>
    FileObject? GetFileObject(FileId fileId);

    /// <summary>Returns the immutable relationship history for a file ID, or null when it is unknown.</summary>
    DirectoryEntryHistory? GetDirectoryEntryHistory(FileId fileId);

    /// <summary>Returns the immutable accepted-event index.</summary>
    ImmutableArray<AppliedStateEvent> GetAppliedEvents();
}

/// <summary>Detects an invalid event before the state engine mutates its indexes.</summary>
public class StateEventValidationException : Exception
{
    /// <summary>Creates a validation exception.</summary>
    public StateEventValidationException(string message) : base(message) { }
}

/// <summary>Indicates that a source sequence skipped one or more events.</summary>
public sealed class SourceSequenceGapException : StateEventValidationException
{
    /// <summary>Creates a source sequence gap exception.</summary>
    public SourceSequenceGapException(string source, long expected, long actual)
        : base($"Source '{source}' skipped sequence values: expected {expected}, received {actual}.")
    {
        Expected = expected;
        Actual = actual;
    }

    /// <summary>Sequence value required to restore continuity.</summary>
    public long Expected { get; }

    /// <summary>Sequence value received after the gap.</summary>
    public long Actual { get; }
}

/// <summary>Indicates that source sequence order moved backwards or conflicted.</summary>
public sealed class SourceSequenceOrderException : StateEventValidationException
{
    /// <summary>Creates a source sequence order exception.</summary>
    public SourceSequenceOrderException(string source, long last, long actual)
        : base($"Source '{source}' moved backwards or reused a sequence: last {last}, received {actual}.")
    {
        Last = last;
        Actual = actual;
    }

    /// <summary>Last accepted sequence value.</summary>
    public long Last { get; }

    /// <summary>Sequence value received out of order.</summary>
    public long Actual { get; }
}

/// <summary>Indicates an impossible parent relationship such as a directory cycle.</summary>
public sealed class StateCorruptionException : StateEventValidationException
{
    /// <summary>Creates a state corruption exception.</summary>
    public StateCorruptionException(string message) : base(message) { }
}

/// <summary>In-memory state engine used by the application state port and deterministic tests.</summary>
public sealed class StateEngine : IStateEngine
{
    private readonly object _gate = new();
    private readonly Dictionary<FileId, List<StoredObjectVersion>> _objects = new();
    private readonly Dictionary<FileId, List<StoredEntryVersion>> _entries = new();
    private readonly Dictionary<SourceCursorKey, SourceCursor> _sourceCursors = new();
    private readonly HashSet<EventId> _appliedIds = new();
    private readonly List<AppliedStateEvent> _appliedEvents = new();
    private DateTimeOffset? _lastEffectiveUtc;
    private long _stateSequence;

    /// <inheritdoc />
    public ValueTask ApplyAsync(CanonicalEvent value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_appliedIds.Contains(value.EventId))
            {
                return ValueTask.CompletedTask;
            }

            ValidateEvent(value);
            var cursorKey = SourceCursorKey.Create(value);
            ValidateSourceSequence(cursorKey, value.Time.SourceSequence.Value);
            var effectiveUtc = GetEffectiveUtc(value.Time.RecordedUtc);
            var nextSequence = checked(_stateSequence + 1);

            if (IsRelationshipOperation(value.Operation) && value.FileId is { } relationshipFileId)
            {
                ValidateNoCycle(relationshipFileId, value.ParentFileId ?? value.Metadata?.ParentFileId, effectiveUtc);
            }

            var objectChanged = TryBuildObjectVersion(value, effectiveUtc, nextSequence, out var objectVersion);
            var relationshipChanged = TryBuildEntryVersion(value, effectiveUtc, nextSequence, out var entryVersion, out var closesEntry);

            if (objectChanged && objectVersion is not null)
            {
                AppendObjectVersion(objectVersion);
            }

            if (relationshipChanged && entryVersion is not null)
            {
                AppendEntryVersion(entryVersion, closesEntry, effectiveUtc);
            }

            _stateSequence = nextSequence;
            _appliedIds.Add(value.EventId);
            _sourceCursors[cursorKey] = new SourceCursor(value.Time.SourceSequence.Value, value.EventId);
            _lastEffectiveUtc = effectiveUtc;
            _appliedEvents.Add(new AppliedStateEvent(value.EventId, value.Operation, GetEventKind(value), effectiveUtc, value.Time.SourceSequence, nextSequence));
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<FileStateSnapshot> GetSnapshotAsync(DateTimeOffset atUtc, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(BuildSnapshot(atUtc, null, cancellationToken));
        }
    }

    /// <inheritdoc />
    public ValueTask<FileStateSnapshot> GetSnapshotAtSequenceAsync(long stateSequence, CancellationToken cancellationToken = default)
    {
        if (stateSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stateSequence), stateSequence, "A state sequence cannot be negative.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var atUtc = _appliedEvents.Where(item => item.StateSequence <= stateSequence).Select(item => item.EffectiveUtc).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
            return ValueTask.FromResult(BuildSnapshot(atUtc, stateSequence, cancellationToken));
        }
    }

    /// <inheritdoc />
    public FileObject? GetFileObject(FileId fileId)
    {
        lock (_gate)
        {
            return !_objects.TryGetValue(fileId, out var versions)
                ? null
                : new FileObject(fileId, versions.Select(item => item.ToPublic()).ToImmutableArray());
        }
    }

    /// <inheritdoc />
    public DirectoryEntryHistory? GetDirectoryEntryHistory(FileId fileId)
    {
        lock (_gate)
        {
            return !_entries.TryGetValue(fileId, out var versions)
                ? null
                : new DirectoryEntryHistory(fileId, versions.Select(item => item.Version).ToImmutableArray());
        }
    }

    /// <inheritdoc />
    public ImmutableArray<AppliedStateEvent> GetAppliedEvents()
    {
        lock (_gate)
        {
            return _appliedEvents.ToImmutableArray();
        }
    }

    private static void ValidateEvent(CanonicalEvent value)
    {
        if (value.EventId.Value == Guid.Empty)
        {
            throw new StateEventValidationException("An event ID is required for idempotent state application.");
        }

        if (value.Time.SourceSequence.Value <= 0)
        {
            throw new StateEventValidationException("Source sequence values must be positive.");
        }

        var requiresFileId = value.Operation is CanonicalOperation.Create or CanonicalOperation.DirectoryCreate or CanonicalOperation.DataWrite or CanonicalOperation.Extend or CanonicalOperation.Truncate or CanonicalOperation.MetadataChanged or CanonicalOperation.SecurityMetadataChanged or CanonicalOperation.Rename or CanonicalOperation.Move or CanonicalOperation.Delete or CanonicalOperation.Recycle or CanonicalOperation.Restore or CanonicalOperation.ReconciliationDiscovered;
        if (requiresFileId && value.FileId is null)
        {
            throw new StateEventValidationException($"Operation {value.Operation} requires a file ID.");
        }
    }

    private void ValidateSourceSequence(SourceCursorKey key, long actual)
    {
        if (!_sourceCursors.TryGetValue(key, out var cursor))
        {
            if (actual != 1)
            {
                throw new SourceSequenceGapException(key.ToString(), 1, actual);
            }

            return;
        }

        if (actual == cursor.LastSequence + 1)
        {
            return;
        }

        if (actual > cursor.LastSequence + 1)
        {
            throw new SourceSequenceGapException(key.ToString(), cursor.LastSequence + 1, actual);
        }

        throw new SourceSequenceOrderException(key.ToString(), cursor.LastSequence, actual);
    }

    private DateTimeOffset GetEffectiveUtc(DateTimeOffset recordedUtc)
    {
        if (_lastEffectiveUtc is not { } last)
        {
            return recordedUtc;
        }

        return recordedUtc < last ? last : recordedUtc;
    }

    private static bool IsRelationshipOperation(CanonicalOperation operation) => operation is CanonicalOperation.Create or CanonicalOperation.DirectoryCreate or CanonicalOperation.Rename or CanonicalOperation.Move or CanonicalOperation.Restore or CanonicalOperation.ReconciliationDiscovered;

    private void ValidateNoCycle(FileId fileId, FileId? parentFileId, DateTimeOffset atUtc)
    {
        if (parentFileId is not { } parent)
        {
            return;
        }

        var seen = new HashSet<FileId> { fileId };
        var current = parent;
        while (true)
        {
            if (!seen.Add(current))
            {
                throw new StateCorruptionException($"Directory relationship for {fileId} would create a cycle.");
            }

            var entry = FindEntry(current, atUtc, null, includeClosedFallback: false);
            if (entry?.Version.ParentFileId is not { } next)
            {
                return;
            }

            current = next;
        }
    }

    private bool TryBuildObjectVersion(CanonicalEvent value, DateTimeOffset effectiveUtc, long stateSequence, out StoredObjectVersion? version)
    {
        version = null;
        if (value.FileId is not { } fileId || value.Operation is CanonicalOperation.MountSession or CanonicalOperation.ShareChanged or CanonicalOperation.SettingsChanged)
        {
            return false;
        }

        var current = FindObject(fileId, effectiveUtc, null, includeClosedFallback: true);
        var metadata = value.Metadata is { } observed
            ? FileObjectMetadataExtensions.From(observed, fileId)
            : current?.Metadata ?? FileObjectMetadataExtensions.Unknown(value.VolumeId ?? current?.Metadata.VolumeId ?? VolumeId.Create("unknown"), fileId);
        var exists = value.Operation is not (CanonicalOperation.Delete or CanonicalOperation.Recycle) && (value.Metadata?.Exists ?? current?.Exists ?? true);
        if (value.Operation == CanonicalOperation.Restore)
        {
            exists = true;
        }

        var quality = value.Quality == EventQuality.Exact && value.Metadata is null && current is not null ? current.Quality : value.Quality;
        version = new StoredObjectVersion(fileId, metadata, effectiveUtc, null, quality, exists, value.Metadata?.InRecycleBin ?? (value.Operation == CanonicalOperation.Recycle), stateSequence);
        return true;
    }

    private bool TryBuildEntryVersion(CanonicalEvent value, DateTimeOffset effectiveUtc, long stateSequence, out StoredEntryVersion? version, out bool closesEntry)
    {
        version = null;
        closesEntry = value.Operation is CanonicalOperation.Delete or CanonicalOperation.Recycle;
        if (value.FileId is not { } fileId || value.Operation is CanonicalOperation.MountSession or CanonicalOperation.ShareChanged or CanonicalOperation.SettingsChanged)
        {
            return false;
        }

        var current = FindEntry(fileId, effectiveUtc, null, includeClosedFallback: true);
        var isDelete = value.Operation is CanonicalOperation.Delete or CanonicalOperation.Recycle;
        var isRelationship = IsRelationshipOperation(value.Operation);
        if (!isDelete && !isRelationship && current is not null)
        {
            return false;
        }

        var hasRelationship = value.Name is not null || value.Metadata is not null || current is not null;
        if (!hasRelationship)
        {
            return false;
        }

        var name = value.Name ?? value.Metadata?.Name ?? current?.Version.Name;
        if (string.IsNullOrEmpty(name))
        {
            throw new StateEventValidationException($"A directory entry name is required for {fileId}.");
        }

        var parent = value.Properties.TryGetValue("parentKnown", out var parentKnown) && string.Equals(parentKnown, "true", StringComparison.OrdinalIgnoreCase)
            ? value.ParentFileId
            : value.ParentFileId ?? value.Metadata?.ParentFileId ?? current?.Version.ParentFileId;
        var parentUnknown = IsParentUnknown(value, parent, current);
        if (parentUnknown)
        {
            parent = null;
        }

        var quality = value.Quality == EventQuality.Exact && value.Metadata is null && current is not null ? current.Version.Quality : value.Quality;
        version = new StoredEntryVersion(new DirectoryEntryVersion(fileId, parent, name, effectiveUtc, null, quality), stateSequence, parentUnknown);
        return true;
    }

    private static bool IsParentUnknown(CanonicalEvent value, FileId? parent, StoredEntryVersion? current)
    {
        if (value.Properties.TryGetValue("parentKnown", out var known) && string.Equals(known, "true", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (value.Properties.TryGetValue("parentUnknown", out var unknown) && string.Equals(unknown, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return parent is null && value.Quality is EventQuality.Unknown or EventQuality.ExistenceOnly or EventQuality.UnverifiedGap && current is null;
    }

    private void AppendObjectVersion(StoredObjectVersion version)
    {
        if (!_objects.TryGetValue(version.FileId, out var versions))
        {
            versions = new List<StoredObjectVersion>();
            _objects.Add(version.FileId, versions);
        }

        CloseObjectVersion(versions, version.EffectiveFromUtc);
        versions.Add(version);
    }

    private void AppendEntryVersion(StoredEntryVersion version, bool closesEntry, DateTimeOffset effectiveUtc)
    {
        if (!_entries.TryGetValue(version.Version.FileId, out var versions))
        {
            versions = new List<StoredEntryVersion>();
            _entries.Add(version.Version.FileId, versions);
        }

        CloseEntryVersion(versions, effectiveUtc);
        if (!closesEntry)
        {
            versions.Add(version);
        }
        else if (IsDirectory(version.Version.FileId, effectiveUtc))
        {
            versions.Add(version with { Version = version.Version with { EffectiveToUtc = null } });
        }
    }

    private static void CloseObjectVersion(List<StoredObjectVersion> versions, DateTimeOffset effectiveUtc)
    {
        if (versions.Count == 0)
        {
            return;
        }

        var current = versions[^1];
        if (current.EffectiveToUtc is null)
        {
            versions[^1] = current with { EffectiveToUtc = effectiveUtc };
        }
    }

    private static void CloseEntryVersion(List<StoredEntryVersion> versions, DateTimeOffset effectiveUtc)
    {
        if (versions.Count == 0)
        {
            return;
        }

        var current = versions[^1];
        if (current.Version.EffectiveToUtc is null)
        {
            versions[^1] = current with { Version = current.Version with { EffectiveToUtc = effectiveUtc } };
        }
    }

    private bool IsDirectory(FileId fileId, DateTimeOffset atUtc)
    {
        var objectVersion = FindObject(fileId, atUtc, null, includeClosedFallback: true);
        return objectVersion?.Metadata.Kind == FileKind.Directory;
    }

    private StoredObjectVersion? FindObject(FileId fileId, DateTimeOffset atUtc, long? stateSequence, bool includeClosedFallback)
    {
        if (!_objects.TryGetValue(fileId, out var versions))
        {
            return null;
        }

        var matching = versions.Where(item => item.StateSequence <= (stateSequence ?? long.MaxValue) && item.EffectiveFromUtc <= atUtc).ToList();
        var current = matching.LastOrDefault(item => item.EffectiveToUtc is null || atUtc < item.EffectiveToUtc.Value);
        return current ?? (includeClosedFallback ? matching.LastOrDefault() : null);
    }

    private StoredEntryVersion? FindEntry(FileId fileId, DateTimeOffset atUtc, long? stateSequence, bool includeClosedFallback)
    {
        if (!_entries.TryGetValue(fileId, out var versions))
        {
            return null;
        }

        var matching = versions.Where(item => item.StateSequence <= (stateSequence ?? long.MaxValue) && item.Version.EffectiveFromUtc <= atUtc).ToList();
        var current = matching.LastOrDefault(item => item.Version.EffectiveToUtc is null || atUtc < item.Version.EffectiveToUtc.Value);
        return current ?? (includeClosedFallback ? matching.LastOrDefault() : null);
    }

    private FileStateSnapshot BuildSnapshot(DateTimeOffset atUtc, long? stateSequence, CancellationToken cancellationToken)
    {
        var entries = new List<FileStateEntry>(_objects.Count);
        var unplaced = new List<FileStateEntry>();
        foreach (var fileId in _objects.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var objectVersion = FindObject(fileId, atUtc, stateSequence, includeClosedFallback: false);
            if (objectVersion is null)
            {
                continue;
            }

            var directoryEntry = FindEntry(fileId, atUtc, stateSequence, includeClosedFallback: objectVersion.Metadata.Kind == FileKind.Directory && !objectVersion.Exists);
            var virtualDeleted = !objectVersion.Exists && objectVersion.Metadata.Kind == FileKind.Directory;
            if (objectVersion.Exists)
            {
                var pathResult = ReconstructPath(fileId, atUtc, stateSequence, cancellationToken);
                virtualDeleted |= pathResult.IsUnderDeletedFolder;
                var stateEntry = CreateStateEntry(objectVersion, directoryEntry, pathResult.Path, virtualDeleted);
                if (directoryEntry?.Version.ParentFileId is null && directoryEntry is not null && !IsKnownRoot(directoryEntry, fileId, atUtc, stateSequence))
                {
                    unplaced.Add(stateEntry);
                }
                else if (pathResult.Path is null)
                {
                    unplaced.Add(stateEntry);
                }
                else
                {
                    entries.Add(stateEntry);
                }
            }
            else if (virtualDeleted)
            {
                var pathResult = ReconstructPath(fileId, atUtc, stateSequence, cancellationToken);
                var stateEntry = CreateStateEntry(objectVersion, directoryEntry, pathResult.Path, true);
                entries.Add(stateEntry);
            }
        }

        return new FileStateSnapshot(atUtc, entries.ToImmutableArray(), unplaced.ToImmutableArray());
    }

    private bool IsKnownRoot(StoredEntryVersion entry, FileId fileId, DateTimeOffset atUtc, long? stateSequence)
    {
        if (entry.ParentUnknown)
        {
            return false;
        }

        var objectVersion = FindObject(fileId, atUtc, stateSequence, includeClosedFallback: true);
        return objectVersion is not null && (objectVersion.Metadata.Kind is FileKind.Directory or FileKind.File) && entry.Version.ParentFileId is null;
    }

    private static FileStateEntry CreateStateEntry(StoredObjectVersion objectVersion, StoredEntryVersion? entry, string? path, bool virtualDeleted)
    {
        var metadata = objectVersion.Metadata.ToDomain(entry?.Version.ParentFileId, entry?.Version.Name ?? string.Empty, objectVersion.Quality, objectVersion.Exists, objectVersion.InRecycleBin);
        return new FileStateEntry(metadata, path, virtualDeleted, objectVersion.Quality);
    }

    private PathResult ReconstructPath(FileId fileId, DateTimeOffset atUtc, long? stateSequence, CancellationToken cancellationToken)
    {
        var components = new List<string>();
        var seen = new HashSet<FileId>();
        var current = fileId;
        var underDeletedFolder = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(current))
            {
                return new PathResult(null, true);
            }

            var objectVersion = FindObject(current, atUtc, stateSequence, includeClosedFallback: true);
            var entry = FindEntry(current, atUtc, stateSequence, includeClosedFallback: objectVersion is not null && objectVersion.Metadata.Kind == FileKind.Directory && !objectVersion.Exists);
            if (entry is null || entry.ParentUnknown)
            {
                return new PathResult(null, underDeletedFolder);
            }

            components.Add(entry.Version.Name);
            if (objectVersion?.Exists == false && objectVersion.Metadata.Kind == FileKind.Directory)
            {
                underDeletedFolder = true;
            }

            if (entry.Version.ParentFileId is not { } parent)
            {
                components.Reverse();
                return new PathResult(string.Join("\\", components), underDeletedFolder);
            }

            var parentObject = FindObject(parent, atUtc, stateSequence, includeClosedFallback: true);
            if (parentObject?.Exists == false && parentObject.Metadata.Kind == FileKind.Directory)
            {
                underDeletedFolder = true;
            }

            current = parent;
        }
    }

    private static StateEventKind GetEventKind(CanonicalEvent value) => value.Operation == CanonicalOperation.UnverifiedGap || value.Quality == EventQuality.UnverifiedGap
        ? StateEventKind.Gap
        : value.Origin is EventOrigin.MftReconciliation or EventOrigin.DirectoryReconciliation || value.Operation == CanonicalOperation.ReconciliationDiscovered
            ? StateEventKind.Reconciliation
            : StateEventKind.Observation;

    private readonly record struct SourceCursorKey(string Volume, string Mount, EventOrigin Origin)
    {
        public static SourceCursorKey Create(CanonicalEvent value) => new(value.VolumeId?.Value ?? value.Metadata?.VolumeId.Value ?? "none", value.MountSessionId?.Value ?? "none", value.Origin);
        public override string ToString() => $"{Volume}/{Mount}/{Origin}";
    }

    private readonly record struct SourceCursor(long LastSequence, EventId LastEventId);
    private sealed record StoredObjectVersion(FileId FileId, FileObjectMetadata Metadata, DateTimeOffset EffectiveFromUtc, DateTimeOffset? EffectiveToUtc, EventQuality Quality, bool Exists, bool InRecycleBin, long StateSequence)
    {
        public FileObjectVersion ToPublic() => new(FileId, Metadata, EffectiveFromUtc, EffectiveToUtc, Quality, Exists, InRecycleBin, StateSequence);
    }

    private sealed record StoredEntryVersion(DirectoryEntryVersion Version, long StateSequence, bool ParentUnknown);
    private readonly record struct PathResult(string? Path, bool IsUnderDeletedFolder);
}

internal static class FileObjectMetadataExtensions
{
    public static FileObjectMetadata From(FileMetadata value, FileId fileId) => new(value.VolumeId, fileId, value.Kind, value.LogicalSize, value.AllocatedSize, value.CreatedUtc, value.LastAccessUtc, value.LastWriteUtc, value.FileSystemChangeUtc, value.Attributes, value.ReparsePointKind, value.CloudPlaceholderState);

    public static FileObjectMetadata Unknown(VolumeId volumeId, FileId fileId) => new(volumeId, fileId, FileKind.Unknown, null, null, null, null, null, null, 0, null, null);

    public static FileMetadata ToDomain(this FileObjectMetadata value, FileId? parentFileId, string name, EventQuality quality, bool exists, bool inRecycleBin) => new(value.VolumeId, value.FileId, parentFileId, name, value.Kind, value.LogicalSize, value.AllocatedSize, value.CreatedUtc, value.LastAccessUtc, value.LastWriteUtc, value.FileSystemChangeUtc, value.Attributes, value.ReparsePointKind, value.CloudPlaceholderState, quality, exists, inRecycleBin);
}
