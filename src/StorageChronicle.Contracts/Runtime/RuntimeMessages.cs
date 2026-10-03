using System.Text.Json;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Contracts.Runtime;

/// <summary>Requests a page of a UI projection.</summary>
public sealed record ProjectionPageRequest(
    EventStackMode Mode,
    int Page,
    int PageSize,
    string? Filter,
    bool Descending = true,
    EventQuality? Quality = null,
    EventOrigin? Origin = null,
    CanonicalOperation? Operation = null,
    IReadOnlyList<string>? AllFilters = null,
    IReadOnlyList<string>? AnyFilters = null,
    IReadOnlyList<string>? ExcludeFilters = null);

/// <summary>Reports a pending reconciliation decision to the UI.</summary>
public sealed record ReconciliationDecision(string RequestId, bool Execute);

/// <summary>Identifies one durable continuity gap awaiting the next UI decision.</summary>
public sealed record PendingReconciliationRequest(
    string RequestId,
    VolumeId? VolumeId,
    string Reason,
    long? SourceSequence,
    DateTimeOffset DiscoveredUtc,
    bool Presented = false,
    string FileSystem = "Unknown",
    DateTimeOffset? GapStartUtc = null);

/// <summary>Describes the protection disclosure for an explicitly requested media-mirror approval.</summary>
public enum MediaMirrorAclDisclosure
{
    /// <summary>The dedicated directory ACL was verified on an NTFS volume.</summary>
    NtfsAclVerified,
    /// <summary>The live volume is NTFS, but its dedicated directory ACL could not be verified.</summary>
    NtfsAclUnavailable,
    /// <summary>The live filesystem does not provide the Windows ACL protection being described.</summary>
    NotProvidedByFileSystem,
    /// <summary>The filesystem or ACL protection could not be verified; approval must not proceed.</summary>
    Unknown
}

/// <summary>Identifies the exact PC, medium, and mirror location awaiting user approval.</summary>
public sealed record PendingMediaMirrorApproval(
    string RequestId,
    string PcIdentity,
    string LogicalMediaId,
    string VolumeId,
    string MediaRoot,
    string DedicatedMediaRootIdentity,
    string FileSystem,
    MediaMirrorAclDisclosure AclDisclosure,
    bool ExistingHistoryReadAndImportRequested,
    bool FutureHistoryAppendRequested,
    DateTimeOffset DiscoveredUtc);

/// <summary>Records the user's explicit decision for a pending media-mirror approval.</summary>
public sealed record MediaMirrorApprovalDecision(string RequestId, bool Approve);

/// <summary>Reports non-user-editable agent health state.</summary>
public sealed record AgentHealth(
    string State,
    string? Reason,
    long LastSequence,
    IReadOnlyList<VolumeHealth> Volumes,
    IReadOnlyList<PendingReconciliationRequest>? PendingReconciliations = null,
    int QueueDepth = 0,
    IReadOnlyList<PendingMediaMirrorApproval>? PendingMediaMirrorApprovals = null);

/// <summary>Reports continuity for one volume.</summary>
public sealed record VolumeHealth(VolumeId VolumeId, MonitoringContinuity Continuity, string? GapReason);

/// <summary>Requests a diff projection over a bounded time range.</summary>
public sealed record DiffProjectionRequest(
    DateTimeOffset? FromUtc,
    DateTimeOffset ToUtc,
    DiffMode Mode,
    string? Filter = null,
    int Page = 1,
    int PageSize = 500,
    IReadOnlyList<string>? AllFilters = null,
    IReadOnlyList<string>? AnyFilters = null,
    IReadOnlyList<string>? ExcludeFilters = null,
    int ActivityFramesPage = 1,
    int ActivityFramesPageSize = 100,
    bool ActivityFramesAscending = false);

/// <summary>Returns one bounded Event Stack page.</summary>
public sealed record ProjectionPageResponse(
    IReadOnlyList<EventStackRow> Items,
    int Page,
    int PageSize,
    int TotalCount,
    bool HasMore,
    IReadOnlyList<EventStackNodeSnapshot>? Nodes = null);

/// <summary>Serializable recursive Event Stack node returned by the Agent.</summary>
public sealed record EventStackNodeSnapshot(
    string NodeId,
    string Kind,
    EventStackRow Row,
    string ProcessDisplayName,
    IReadOnlyList<EventStackNodeSnapshot> Children);

/// <summary>Returns one bounded Diff View page.</summary>
public sealed record DiffProjectionResponse(
    IReadOnlyList<DiffEntry> Items,
    IReadOnlyList<DiffProjectionItemSnapshot>? RichItems = null,
    IReadOnlyList<DiffProjectionItemSnapshot>? RichUnknownLocationItems = null,
    int Page = 1,
    int PageSize = 500,
    int TotalCount = 0,
    bool HasMore = false,
    IReadOnlyList<DiffActivityFrameSnapshot>? ActivityFrames = null,
    int ActivityFramesPage = 1,
    int ActivityFramesPageSize = 100,
    int ActivityFramesTotalCount = 0,
    bool HasMoreActivityFrames = false);

/// <summary>Metadata-only Activity Group projection used to render one Diff View frame, including an optional observed process-exit boundary.</summary>
public sealed record DiffActivityFrameSnapshot(
    string FrameId,
    ProcessInstanceId? ProcessId,
    string ProcessDisplayName,
    ProcessAttributionQuality ProcessQuality,
    EventOrigin Source,
    VolumeId? VolumeId,
    MountSessionId? MountSessionId,
    string DisplayRoute,
    DateTimeOffset StartedUtc,
    DateTimeOffset LastEventUtc,
    DateTimeOffset CloseBoundaryUtc,
    bool IsClosedByCompetingActivity,
    int OperationCount,
    int FileCount,
    long SizeDelta,
    IReadOnlyDictionary<string, int> OperationBreakdown,
    int EventCount,
    bool IsClosedByProcessExit = false);

/// <summary>Requests one bounded metadata-only timeline page for an Activity Frame.</summary>
public sealed record DiffActivityFrameTimelineRequest(
    string FrameId,
    DateTimeOffset? FromUtc,
    DateTimeOffset ToUtc,
    int Page = 1,
    int PageSize = 250);

/// <summary>Returns a bounded metadata-only event timeline page for one Activity Frame.</summary>
public sealed record DiffActivityFrameTimelineResponse(
    string FrameId,
    IReadOnlyList<DiffActivityFrameEventSnapshot> Events,
    int Page,
    int PageSize,
    int TotalCount,
    bool HasMore);

/// <summary>One metadata-only event used to replay a Diff View frame.</summary>
public sealed record DiffActivityFrameEventSnapshot(
    EventId EventId,
    DateTimeOffset TimeUtc,
    string DisplayPath,
    CanonicalOperation Operation,
    EventQuality Quality);

/// <summary>Serializable Tree/Explorer diff row returned by the Agent.</summary>
public sealed record DiffProjectionItemSnapshot(
    FileId? FileId,
    string DisplayName,
    string? OldPath,
    string? NewPath,
    string DisplayPath,
    FileKind Kind,
    EventQuality Quality,
    string PrimaryOperation,
    string SemanticState,
    IReadOnlyList<string> SubOperations,
    string? RelatedOperationId,
    bool IsVirtual,
    bool IsDeleted,
    bool IsPeriodOnly,
    bool CanOpenInExplorer,
    string? OpenDisabledReason,
    int DescendantCount,
    IReadOnlyList<string> RenameHistory,
    IReadOnlyList<ReplayTimelinePointSnapshot> ReplayTimeline,
    IReadOnlyList<EventId> EventIds);

/// <summary>Serializable replay timeline point for one diff row.</summary>
public sealed record ReplayTimelinePointSnapshot(EventId EventId, DateTimeOffset TimeUtc, string Lifecycle, string Operation);

/// <summary>Requests the current non-user-editable agent state.</summary>
public sealed record AgentHealthRequest;

/// <summary>Requests bounded detail data for one selected Event Stack row.</summary>
public sealed record EventDetailsRequest(EventId EventId);

/// <summary>Returns detail data for one selected Event Stack row.</summary>
public sealed record EventDetailsResponse(EventDetailsSnapshot? Details);

/// <summary>Requests a persisted settings snapshot through the Agent.</summary>
public sealed record SettingsSnapshotRequest;

/// <summary>Identifies a settings scope in an IPC request.</summary>
public enum SettingsScope
{
    /// <summary>Machine-wide Agent-owned settings.</summary>
    Machine,
    /// <summary>Interactive-user settings.</summary>
    User
}

/// <summary>Requests a validated settings update. The Agent maps this DTO to its internal settings model.</summary>
public sealed record SettingsUpdateRequest(SettingsScope Scope, JsonElement Settings);

/// <summary>Returns both settings scopes and recovery warnings without exposing machine paths through UI file I/O.</summary>
public sealed record SettingsSnapshot(JsonElement Machine, JsonElement User, string? MachineWarning, string? UserWarning);

/// <summary>Bounded clipboard metadata sent by the interactive Session Agent; no clipboard bytes are transported.</summary>
public sealed record ClipboardCandidateRequest(long Generation, IReadOnlyList<string> Paths, bool IsCut, string Quality, DateTimeOffset ObservedUtc, long SourceSequence);
