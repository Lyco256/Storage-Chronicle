using System.Collections.Immutable;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Projection;

/// <summary>Names of optional, OS-neutral properties understood by the projection layer.</summary>
public static class ProjectionPropertyNames
{
    /// <summary>The resolved full path of the item at event time.</summary>
    public const string Path = "path";
    /// <summary>The path before a rename or move.</summary>
    public const string OldPath = "oldPath";
    /// <summary>The process display name.</summary>
    public const string ProcessName = "process.name";
    /// <summary>The process executable path.</summary>
    public const string ExecutablePath = "process.executable";
    /// <summary>The parent process-instance identifier.</summary>
    public const string ParentProcessInstanceId = "process.parentInstanceId";
    /// <summary>A source-specific display name.</summary>
    public const string SourceName = "source.name";
    /// <summary>A stable link between a source event and its normalized event.</summary>
    public const string NormalizedEventId = "normalizedEventId";
    /// <summary>A related operation identifier used to join move endpoints.</summary>
    public const string RelatedOperationId = "relatedOperationId";
    /// <summary>A textual change reason retained for metadata projections.</summary>
    public const string ChangeReason = "changeReason";
}

/// <summary>Sort order used by event-stack and activity projections.</summary>
public enum ProjectionSortOrder
{
    /// <summary>Newest values first.</summary>
    Descending,
    /// <summary>Oldest values first.</summary>
    Ascending
}

/// <summary>Projection node level in the expandable Event Stack.</summary>
public enum EventStackNodeKind
{
    /// <summary>Grouped activity node.</summary>
    Activity,
    /// <summary>File-level summary node.</summary>
    FileSummary,
    /// <summary>Normalized event node.</summary>
    Normalized,
    /// <summary>Source event node.</summary>
    Source,
    /// <summary>Continuity-gap node.</summary>
    Gap
}

/// <summary>Immutable input for all projections. The lists are copied so projection is repeatable.</summary>
public sealed class ProjectionDocument
{
    /// <summary>Creates a document from canonical events and optional source envelopes.</summary>
    public ProjectionDocument(IEnumerable<CanonicalEvent> canonicalEvents, IEnumerable<SourceEvent>? sourceEvents = null)
    {
        ArgumentNullException.ThrowIfNull(canonicalEvents);
        CanonicalEvents = canonicalEvents.ToArray();
        SourceEvents = (sourceEvents ?? Enumerable.Empty<SourceEvent>()).ToArray();
    }

    /// <summary>Canonical events used to build normalized, grouped, and diff projections.</summary>
    public IReadOnlyList<CanonicalEvent> CanonicalEvents { get; }
    /// <summary>Source envelopes used by Source mode and normalized-to-source expansion.</summary>
    public IReadOnlyList<SourceEvent> SourceEvents { get; }
}

/// <summary>Validated settings for activity grouping and Event Stack pages.</summary>
public sealed record ProjectionSettings
{
    /// <summary>Creates settings using the product defaults.</summary>
    public ProjectionSettings(TimeSpan? activityTimeout = null, int eventStackPageSize = 250)
    {
        ActivityTimeout = activityTimeout ?? TimeSpan.FromSeconds(2);
        if (ActivityTimeout < TimeSpan.FromSeconds(0.5) || ActivityTimeout > TimeSpan.FromSeconds(60))
        {
            throw new ArgumentOutOfRangeException(nameof(activityTimeout), "Activity timeout must be between 0.5 and 60 seconds.");
        }

        if (eventStackPageSize is < 50 or > 5000)
        {
            throw new ArgumentOutOfRangeException(nameof(eventStackPageSize), "Event Stack page size must be between 50 and 5000 rows.");
        }

        EventStackPageSize = eventStackPageSize;
    }

    /// <summary>Maximum inactivity interval for one activity.</summary>
    public TimeSpan ActivityTimeout { get; }
    /// <summary>Default Event Stack page size.</summary>
    public int EventStackPageSize { get; }
}

/// <summary>A query for an Event Stack page.</summary>
public sealed record EventStackQuery
{
    /// <summary>Creates a validated query.</summary>
    public EventStackQuery(EventStackMode mode, int page = 1, int pageSize = 250, ProjectionSortOrder sortOrder = ProjectionSortOrder.Descending, ProjectionFilter? filter = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 5000);
        Mode = mode;
        Page = page;
        PageSize = pageSize;
        SortOrder = sortOrder;
        Filter = filter ?? ProjectionFilter.Empty;
    }

    /// <summary>Requested Event Stack mode.</summary>
    public EventStackMode Mode { get; }
    /// <summary>One-based page number.</summary>
    public int Page { get; }
    /// <summary>Maximum root rows in a page.</summary>
    public int PageSize { get; }
    /// <summary>Requested time order.</summary>
    public ProjectionSortOrder SortOrder { get; }
    /// <summary>Optional literal filter.</summary>
    public ProjectionFilter Filter { get; }
}

/// <summary>Process attribution details displayed by a row or activity title.</summary>
public sealed record ProcessProjection(
    ProcessInstanceId? Id,
    string DisplayName,
    string? ExecutablePath,
    ProcessAttributionQuality Quality,
    ProcessInstanceId? ParentProcessInstanceId,
    IReadOnlyList<ProcessInstanceId> Ancestors,
    IReadOnlyList<ProcessInstanceId> Children);

/// <summary>One expandable Event Stack node. Children are kept under this root for atomic paging.</summary>
public sealed record EventStackNode(
    string NodeId,
    EventStackNodeKind Kind,
    EventStackRow Row,
    string ProcessDisplayName,
    ProcessProjection? Process,
    IReadOnlyList<EventStackNode> Children);

/// <summary>Operation counts and size delta for an activity.</summary>
public sealed record ActivityMetrics(
    int OperationCount,
    int FileCount,
    long SizeDelta,
    TimeSpan Duration,
    IReadOnlyDictionary<CanonicalOperation, int> OperationBreakdown,
    int UnverifiedGapCount);

/// <summary>One file summary inside a grouped Event Stack activity.</summary>
public sealed record EventStackFileSummary(
    FileId? FileId,
    string DisplayPath,
    CanonicalOperation PrimaryOperation,
    int OperationCount,
    long SizeDelta,
    IReadOnlyList<EventId> EventIds);

/// <summary>Deterministic grouped activity projection.</summary>
public sealed record ActivityGroupProjection(
    string GroupId,
    ProcessProjection? Process,
    string ProcessDisplayName,
    ProcessAttributionQuality ProcessQuality,
    EventOrigin Source,
    VolumeId? Volume,
    MountSessionId? MountSession,
    string DisplayRoute,
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    bool IsClosedByCompetingActivity,
    ActivityMetrics Metrics,
    IReadOnlyList<EventStackFileSummary> Files,
    IReadOnlyList<CanonicalEvent> Events);

/// <summary>Semantic primary operation used by a file-system diff row.</summary>
public enum DiffPrimaryOperation
{
    /// <summary>Delete operation.</summary>
    Delete,
    /// <summary>Recycle operation.</summary>
    Recycle,
    /// <summary>Restore operation.</summary>
    Restore,
    /// <summary>Move source endpoint.</summary>
    MoveFrom,
    /// <summary>Move destination endpoint.</summary>
    MoveTo,
    /// <summary>Copy operation.</summary>
    Copy,
    /// <summary>Create operation.</summary>
    Create,
    /// <summary>Rename operation.</summary>
    Rename,
    /// <summary>Data write operation.</summary>
    DataWrite,
    /// <summary>Resize operation.</summary>
    Resize,
    /// <summary>Truncate operation.</summary>
    Truncate,
    /// <summary>Share operation.</summary>
    Share,
    /// <summary>Cloud-state operation.</summary>
    CloudState,
    /// <summary>Metadata operation.</summary>
    MetadataChange,
    /// <summary>Unknown operation.</summary>
    Unknown
}

/// <summary>Meaning of the primary and secondary visual markers. It is not a UI color value.</summary>
public enum DiffSemanticState
{
    /// <summary>Added state.</summary>
    Added,
    /// <summary>Removed state.</summary>
    Removed,
    /// <summary>Edited state.</summary>
    Edited,
    /// <summary>Moved state.</summary>
    Moved,
    /// <summary>Renamed state.</summary>
    Renamed,
    /// <summary>Shared state.</summary>
    Shared,
    /// <summary>Cloud state.</summary>
    Cloud,
    /// <summary>Metadata-only state.</summary>
    Metadata,
    /// <summary>Reconciled state.</summary>
    Reconciled,
    /// <summary>Unknown state.</summary>
    Unknown
}

/// <summary>Replay lifecycle of one pane at one event time.</summary>
public enum PaneLifecycle
{
    /// <summary>The row appeared in the pane.</summary>
    Appeared,
    /// <summary>The row was updated.</summary>
    Updated,
    /// <summary>The row ended.</summary>
    Ended
}

/// <summary>One event-level replay timeline point.</summary>
public sealed record ReplayTimelinePoint(EventId EventId, DateTimeOffset TimeUtc, PaneLifecycle Lifecycle, DiffPrimaryOperation Operation);

/// <summary>Detailed file-system projection shared by Tree and Explorer views.</summary>
public sealed record FileDiffProjection(
    FileId? FileId,
    string DisplayName,
    string? OldPath,
    string? NewPath,
    string DisplayPath,
    FileKind Kind,
    EventQuality Quality,
    DiffPrimaryOperation PrimaryOperation,
    DiffSemanticState SemanticState,
    IReadOnlyList<DiffPrimaryOperation> SubOperations,
    string? RelatedOperationId,
    bool IsVirtual,
    bool IsDeleted,
    bool IsPeriodOnly,
    bool CanOpenInExplorer,
    string? OpenDisabledReason,
    int DescendantCount,
    IReadOnlyList<string> RenameHistory,
    IReadOnlyList<ReplayTimelinePoint> ReplayTimeline,
    IReadOnlyList<EventId> EventIds);

/// <summary>Diff result containing both detailed rows and the shared tree/explorer projection.</summary>
public sealed record DiffProjection(
    DiffMode Mode,
    DateTimeOffset ToUtc,
    IReadOnlyList<FileDiffProjection> Items,
    IReadOnlyList<FileDiffProjection> UnknownLocationItems,
    IReadOnlyList<DiffEntry> DomainEntries);

/// <summary>Stable filter fields exposed by the product requirements.</summary>
public enum FilterField
{
    /// <summary>Event time.</summary>
    Time,
    /// <summary>Display name.</summary>
    Name,
    /// <summary>Display path.</summary>
    Path,
    /// <summary>File extension.</summary>
    Extension,
    /// <summary>Operation.</summary>
    Operation,
    /// <summary>Process identity.</summary>
    Process,
    /// <summary>Executable identity.</summary>
    Executable,
    /// <summary>Parent process identity.</summary>
    ParentProcess,
    /// <summary>Source origin.</summary>
    Source,
    /// <summary>Monitoring quality.</summary>
    MonitoringQuality,
    /// <summary>Metadata quality.</summary>
    MetadataQuality,
    /// <summary>Volume.</summary>
    Volume,
    /// <summary>Deleted state.</summary>
    Deleted,
    /// <summary>Recycle-bin state.</summary>
    RecycleBin,
    /// <summary>Shared state.</summary>
    Shared,
    /// <summary>Full-reconciliation difference.</summary>
    FullReconciliationDifference,
    /// <summary>Unknown process state.</summary>
    UnknownProcess,
    /// <summary>Object size.</summary>
    Size
}

/// <summary>Filter syntax reserved for the current literal matcher and a future regex matcher.</summary>
public enum FilterMatchKind
{
    /// <summary>Literal matching.</summary>
    Literal,
    /// <summary>Regular-expression matching.</summary>
    Regex
}

/// <summary>One literal filter term.</summary>
public sealed record FilterTerm
{
    /// <summary>Creates and validates a literal or regex filter term.</summary>
    public FilterTerm(FilterField field, string value, FilterMatchKind matchKind = FilterMatchKind.Literal)
    {
        Field = field;
        Value = string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A filter value is required.", nameof(value)) : value;
        MatchKind = matchKind;
    }

    /// <summary>Filter field.</summary>
    public FilterField Field { get; }
    /// <summary>Literal or regex value.</summary>
    public string Value { get; }
    /// <summary>Matching mode.</summary>
    public FilterMatchKind MatchKind { get; }
}

/// <summary>Composable AND, OR, and exclusion filter DTO.</summary>
public sealed record ProjectionFilter(
    IReadOnlyList<FilterTerm>? All = null,
    IReadOnlyList<FilterTerm>? Any = null,
    IReadOnlyList<FilterTerm>? Exclude = null)
{
    /// <summary>An empty filter which matches every item.</summary>
    public static ProjectionFilter Empty { get; } = new();
    /// <summary>AND terms.</summary>
    public IReadOnlyList<FilterTerm> AllTerms { get; } = All ?? Array.Empty<FilterTerm>();
    /// <summary>OR terms.</summary>
    public IReadOnlyList<FilterTerm> AnyTerms { get; } = Any ?? Array.Empty<FilterTerm>();
    /// <summary>Excluded terms.</summary>
    public IReadOnlyList<FilterTerm> ExcludedTerms { get; } = Exclude ?? Array.Empty<FilterTerm>();
}

/// <summary>A versioned saved-filter DTO safe to persist outside the projection process.</summary>
public sealed record SavedProjectionFilter(string Id, string Name, ProjectionFilter Filter, int Version = 1);

/// <summary>Values made available to a filter matcher.</summary>
public sealed record ProjectionFilterContext(IReadOnlyDictionary<FilterField, string> Values);

/// <summary>Extension point for a future regex matcher without changing the saved-filter contract.</summary>
public interface IProjectionFilterMatcher
{
    /// <summary>Matches one term against a filter context.</summary>
    bool Matches(FilterTerm term, ProjectionFilterContext context);
}
