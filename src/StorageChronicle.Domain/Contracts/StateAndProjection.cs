namespace StorageChronicle.Domain.Contracts;

/// <summary>Represents a state snapshot at a requested time.</summary>
public sealed record FileStateSnapshot(DateTimeOffset AtUtc, IReadOnlyList<FileStateEntry> Entries, IReadOnlyList<FileStateEntry> UnplacedEntries);

/// <summary>Represents one file-system object in a state snapshot.</summary>
public sealed record FileStateEntry(FileMetadata Metadata, string? ReconstructedPath, bool IsVirtualDeleted, EventQuality Quality);

/// <summary>Describes a diff row derived from two state snapshots.</summary>
public sealed record DiffEntry(FileId? FileId, string? OldPath, string? NewPath, CanonicalOperation Operation, FileKind Kind, EventQuality Quality, bool IsVirtual);

/// <summary>Describes a source, normalized, or grouped event-stack row.</summary>
public sealed record EventStackRow(EventId Id, DateTimeOffset TimeUtc, string DisplayRoute, CanonicalOperation Operation, string Summary, EventQuality Quality, ProcessInstanceId? ProcessId, ProcessAttributionQuality ProcessQuality, EventOrigin Origin, IReadOnlyList<EventId> Children);

/// <summary>Bounded detail data for one Event Stack selection.</summary>
public sealed record EventDetailsSnapshot(
    EventStackRow Row,
    EventOrigin SourceOrigin,
    EventQuality Quality,
    DateTimeOffset RecordedUtc,
    TimeSpan LocalOffset,
    SourceSequence SourceSequence,
    MountSequence MountSequence,
    string? ProcessName,
    ProcessInstanceId? ParentProcess,
    IReadOnlyList<ProcessInstanceId> ChildProcesses,
    bool IsReconciliation,
    bool IsUnverifiedGap,
    bool IsExistenceOnly);

/// <summary>Controls which event-stack projection is returned.</summary>
public enum EventStackMode { Source, Normalized, Grouped }

/// <summary>Controls the diff projection time semantics.</summary>
public enum DiffMode { Live, Period, PointInTime, Replay }

/// <summary>Describes a bounded page of projection rows.</summary>
public sealed record ProjectionPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount, bool HasMore);
