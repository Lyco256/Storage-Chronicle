using System.Collections.Immutable;

namespace StorageChronicle.Domain.Contracts;

/// <summary>Identifies a local volume without relying on a drive letter.</summary>
public readonly record struct VolumeId(string Value)
{
    /// <summary>Returns a validated volume identifier.</summary>
    public static VolumeId Create(string value) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A volume identifier is required.", nameof(value)) : new(value);
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Identifies a file-system object for the lifetime in which its identity is known.</summary>
public readonly record struct FileId(string Value)
{
    /// <summary>Returns a validated file identifier.</summary>
    public static FileId Create(string value) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A file identifier is required.", nameof(value)) : new(value);
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Identifies one process instance, preventing PID reuse from joining activities.</summary>
public readonly record struct ProcessInstanceId(string Value)
{
    /// <summary>Returns a validated process-instance identifier.</summary>
    public static ProcessInstanceId Create(string value) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A process instance identifier is required.", nameof(value)) : new(value);
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Identifies a user-session mount of a volume.</summary>
public readonly record struct MountSessionId(string Value)
{
    /// <summary>Returns a validated mount-session identifier.</summary>
    public static MountSessionId Create(string value) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A mount session identifier is required.", nameof(value)) : new(value);
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Identifies a history branch created by independent writers or recovery.</summary>
public readonly record struct HistoryBranchId(string Value)
{
    /// <summary>Returns a validated history-branch identifier.</summary>
    public static HistoryBranchId Create(string value) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A branch identifier is required.", nameof(value)) : new(value);
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Identifies an immutable event.</summary>
public readonly record struct EventId(Guid Value)
{
    /// <summary>Creates a new event identifier.</summary>
    public static EventId New() => new(Guid.NewGuid());
    /// <inheritdoc />
    public override string ToString() => Value.ToString("N");
}

/// <summary>Identifies an event schema version.</summary>
public readonly record struct EventSchemaVersion(int Major, int Minor)
{
    /// <summary>The current schema used by the MVP.</summary>
    public static EventSchemaVersion Current => new(1, 0);
}

/// <summary>Identifies a source-local monotonic sequence.</summary>
public readonly record struct SourceSequence(long Value);

/// <summary>Identifies a mount-local monotonic sequence.</summary>
public readonly record struct MountSequence(long Value);

/// <summary>Describes where an event was acquired.</summary>
public enum EventOrigin
{
    /// <summary>Live NTFS USN record.</summary>
    LiveUsn,
    /// <summary>USN record recovered after interruption.</summary>
    RecoveredUsn,
    /// <summary>ETW observation retained only for bounded correlation.</summary>
    Etw,
    /// <summary>User-session clipboard observation.</summary>
    Clipboard,
    /// <summary>Initial state enumeration.</summary>
    InitialSnapshot,
    /// <summary>NTFS reconciliation result.</summary>
    MftReconciliation,
    /// <summary>Directory reconciliation result.</summary>
    DirectoryReconciliation,
    /// <summary>SMB share state observation.</summary>
    ShareSnapshot,
    /// <summary>SMB share state change.</summary>
    ShareChange,
    /// <summary>Future driver observation.</summary>
    Driver
}

/// <summary>Describes how confidently a value was observed.</summary>
public enum EventQuality
{
    /// <summary>Observed directly from a reliable source.</summary>
    Exact,
    /// <summary>Correlated from bounded observations.</summary>
    Correlated,
    /// <summary>Known to be incomplete.</summary>
    Unknown,
    /// <summary>Only existence could be established.</summary>
    ExistenceOnly,
    /// <summary>Only the journal record was available.</summary>
    JournalOnly,
    /// <summary>Produced by state reconciliation.</summary>
    Reconciled,
    /// <summary>Continuity was not verified.</summary>
    UnverifiedGap
}

/// <summary>Describes process attribution confidence.</summary>
public enum ProcessAttributionQuality
{
    /// <summary>The process identity is exact.</summary>
    Exact,
    /// <summary>The process identity is correlated from bounded metadata.</summary>
    Correlated,
    /// <summary>The process identity is unavailable.</summary>
    Unknown
}

/// <summary>Describes the operation represented by a canonical event.</summary>
public enum CanonicalOperation
{
    /// <summary>Creates a file.</summary>
    Create,
    /// <summary>Creates a directory.</summary>
    DirectoryCreate,
    /// <summary>Writes file data.</summary>
    DataWrite,
    /// <summary>Extends file data.</summary>
    Extend,
    /// <summary>Truncates file data.</summary>
    Truncate,
    /// <summary>Changes non-security metadata.</summary>
    MetadataChanged,
    /// <summary>Changes security metadata.</summary>
    SecurityMetadataChanged,
    /// <summary>Renames an object.</summary>
    Rename,
    /// <summary>Moves an object.</summary>
    Move,
    /// <summary>Deletes an object.</summary>
    Delete,
    /// <summary>Moves an object to the recycle bin.</summary>
    Recycle,
    /// <summary>Restores an object.</summary>
    Restore,
    /// <summary>Changes share metadata.</summary>
    ShareChanged,
    /// <summary>Changes cloud-state metadata.</summary>
    CloudStateChanged,
    /// <summary>Discovers a reconciliation condition.</summary>
    ReconciliationDiscovered,
    /// <summary>Records an unverified continuity gap.</summary>
    UnverifiedGap,
    /// <summary>Records that recording stopped.</summary>
    RecordingStopped,
    /// <summary>Records a settings change.</summary>
    SettingsChanged,
    /// <summary>Records a mount-session event.</summary>
    MountSession
}

/// <summary>Describes the type of file-system object.</summary>
public enum FileKind
{
    /// <summary>Regular file.</summary>
    File,
    /// <summary>Directory.</summary>
    Directory,
    /// <summary>Symbolic link.</summary>
    SymbolicLink,
    /// <summary>Junction.</summary>
    Junction,
    /// <summary>Other reparse point.</summary>
    ReparsePoint,
    /// <summary>Unknown object kind.</summary>
    Unknown
}

/// <summary>Describes monitoring continuity for a volume or mount.</summary>
public enum MonitoringContinuity
{
    /// <summary>Events were observed continuously.</summary>
    Continuous,
    /// <summary>Continuity was recovered from a journal.</summary>
    JournalRecovered,
    /// <summary>Continuity was supplied by another PC's mirror.</summary>
    MirroredFromAnotherPc,
    /// <summary>Continuity was reconstructed from state.</summary>
    ReconciledState,
    /// <summary>Continuity remains unverified.</summary>
    UnverifiedGap
}

/// <summary>Records all clocks and source ordering values without correcting the machine clock.</summary>
public sealed record EventTime(
    DateTimeOffset RecordedUtc,
    TimeSpan LocalOffset,
    DateTimeOffset? SourceTime,
    DateTimeOffset ReceivedUtc,
    SourceSequence SourceSequence,
    MountSequence MountSequence);

/// <summary>Metadata observed for a file-system object. It deliberately has no content or content hash.</summary>
public sealed record FileMetadata(
    VolumeId VolumeId,
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
    string? CloudPlaceholderState,
    EventQuality Quality,
    bool Exists,
    bool InRecycleBin);

/// <summary>Represents the bounded set of source facts used to produce a canonical event.</summary>
public sealed record SourceEvent(
    EventId EventId,
    EventSchemaVersion SchemaVersion,
    EventOrigin Origin,
    VolumeId? VolumeId,
    FileId? FileId,
    FileId? ParentFileId,
    string? Name,
    string? OldName,
    CanonicalOperation? Hint,
    FileMetadata? Metadata,
    EventTime Time,
    EventQuality Quality,
    ProcessInstanceId? ProcessInstanceId,
    ProcessAttributionQuality ProcessQuality,
    MountSessionId? MountSessionId,
    string? OperationCorrelationId,
    ImmutableDictionary<string, string> Properties);

/// <summary>Represents an immutable, OS-neutral fact in the durable event history.</summary>
public sealed record CanonicalEvent(
    EventId EventId,
    EventSchemaVersion SchemaVersion,
    CanonicalOperation Operation,
    EventOrigin Origin,
    VolumeId? VolumeId,
    FileId? FileId,
    FileId? ParentFileId,
    string? Name,
    string? OldName,
    FileMetadata? Metadata,
    EventTime Time,
    EventQuality Quality,
    ProcessInstanceId? ProcessInstanceId,
    ProcessAttributionQuality ProcessQuality,
    MountSessionId? MountSessionId,
    string? OperationCorrelationId,
    ImmutableDictionary<string, string> Properties)
{
    /// <summary>Returns true when the event is a read-only observation that must not be durable.</summary>
    public bool IsReadOnlyObservation => Origin == EventOrigin.Etw && HintIsReadOnly();

    private bool HintIsReadOnly() => Properties.TryGetValue("observation", out var value) &&
                                     value is "Read" or "Open" or "Query" or "DirectoryEnumeration";
}

/// <summary>Describes an interval during which an entry occupied a parent/name relationship.</summary>
public sealed record DirectoryEntryVersion(FileId FileId, FileId? ParentFileId, string Name, DateTimeOffset EffectiveFromUtc, DateTimeOffset? EffectiveToUtc, EventQuality Quality);

/// <summary>Describes a period in which a continuity gap is not verified.</summary>
public sealed record ReconciliationGap(VolumeId VolumeId, DateTimeOffset StartedUtc, DateTimeOffset DiscoveredUtc, string Reason, bool UserDeclined);

/// <summary>Identifies a physical or logical media mount.</summary>
public sealed record MountSession(MountSessionId Id, VolumeId VolumeId, string PcId, DateTimeOffset ConnectedUtc, DateTimeOffset? DisconnectedUtc, MountSessionId? PreviousSession, MonitoringContinuity Continuity, ImmutableArray<string> SegmentReferences);

/// <summary>Describes the semantic operation icon selected by the UI.</summary>
public enum OperationIconMeaning
{
    /// <summary>Created operation.</summary>
    Created,
    /// <summary>Edited operation.</summary>
    Edited,
    /// <summary>Moved operation.</summary>
    Moved,
    /// <summary>Renamed operation.</summary>
    Renamed,
    /// <summary>Deleted operation.</summary>
    Deleted,
    /// <summary>Recycled operation.</summary>
    Recycled,
    /// <summary>Restored operation.</summary>
    Restored,
    /// <summary>Shared operation.</summary>
    Shared,
    /// <summary>Reconciled operation.</summary>
    Reconciled,
    /// <summary>Unknown operation.</summary>
    Unknown
}
