using StorageChronicle.Domain.Contracts;
using StorageChronicle.UI.Shared;

namespace StorageChronicle.UI.EventStack;

/// <summary>Controls the ordering applied by the Event Stack projection.</summary>
public enum EventStackSortDirection
{
    /// <summary>Newest events appear first.</summary>
    Descending,
    /// <summary>Oldest events appear first.</summary>
    Ascending
}

/// <summary>Names the keyboard actions handled without coupling the view model to Avalonia input types.</summary>
public enum EventStackKey
{
    /// <summary>Moves selection to the previous row.</summary>
    Up,
    /// <summary>Moves selection to the next row.</summary>
    Down,
    /// <summary>Expands or selects the current row.</summary>
    Enter,
    /// <summary>Expands the current row.</summary>
    Right,
    /// <summary>Collapses the current row.</summary>
    Left,
    /// <summary>Moves to the previous page.</summary>
    PageUp,
    /// <summary>Moves to the next page.</summary>
    PageDown,
    /// <summary>Moves to the first page.</summary>
    Home,
    /// <summary>Moves to the last known page.</summary>
    End,
    /// <summary>Closes the detail panel.</summary>
    Escape
}

/// <summary>Represents the bounded filter state shared by all Event Stack modes.</summary>
public sealed record EventStackFilter(
    string SearchText,
    EventQuality? Quality,
    EventOrigin? Origin,
    CanonicalOperation? Operation)
{
    /// <summary>An empty filter.</summary>
    public static EventStackFilter Empty { get; } = new(string.Empty, null, null, null);
}

/// <summary>Represents one query sent to the platform-neutral projection boundary.</summary>
public sealed record EventStackQuery(
    EventStackMode Mode,
    int Page,
    int PageSize,
    EventStackSortDirection SortDirection,
    EventStackFilter Filter,
    IReadOnlySet<EventId> ExpandedGroups)
{
    /// <summary>Creates the initial grouped, newest-first query.</summary>
    public static EventStackQuery Initial(int pageSize = 250) => new(EventStackMode.Grouped, 1, pageSize, EventStackSortDirection.Descending, EventStackFilter.Empty, new HashSet<EventId>());
}

/// <summary>Describes a row and its bounded expansion children.</summary>
public sealed record EventStackItem(
    EventStackRow Row,
    IReadOnlyList<EventStackRow> Children,
    string? FileSummary,
    string? ProcessName,
    bool IsGroup,
    bool IsExpanded,
    IReadOnlyList<EventStackItem>? NestedChildren = null);

/// <summary>Contains one page of materialized Event Stack rows.</summary>
public sealed record EventStackPage(
    IReadOnlyList<EventStackItem> Items,
    int Page,
    int PageSize,
    int TotalCount,
    bool HasMore);

/// <summary>Contains source-quality and process navigation details for a selected row.</summary>
public sealed record EventStackDetails(
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

/// <summary>Represents a saved filter choice exposed by the UI.</summary>
public sealed record SavedEventStackFilter(string Name, EventStackFilter Filter);

/// <summary>Provides only bounded Event Stack pages and selected-row details.</summary>
public interface IEventStackProjection
{
    /// <summary>Loads one page for the complete mode/filter/sort/expansion query.</summary>
    ValueTask<EventStackPage> GetPageAsync(EventStackQuery query, CancellationToken cancellationToken = default);
    /// <summary>Loads detail data for one selected event.</summary>
    ValueTask<EventStackDetails?> GetDetailsAsync(EventId eventId, CancellationToken cancellationToken = default);
}

/// <summary>Adapts the shared page contract for callers that have not yet adopted Event Stack query details.</summary>
public sealed class EventStackPageSourceAdapter : IEventStackProjection
{
    private readonly IVirtualizedPageSource<EventStackRow> source;

    /// <summary>Initializes an adapter over a bounded shared page source.</summary>
    public EventStackPageSourceAdapter(IVirtualizedPageSource<EventStackRow> source) => this.source = source ?? throw new ArgumentNullException(nameof(source));

    /// <inheritdoc />
    public async ValueTask<EventStackPage> GetPageAsync(EventStackQuery query, CancellationToken cancellationToken = default)
    {
        var page = await source.GetPageAsync(query.Page, query.PageSize, cancellationToken).ConfigureAwait(false);
        return new EventStackPage(page.Items.Select(row => new EventStackItem(row, Array.Empty<EventStackRow>(), null, null, query.Mode == EventStackMode.Grouped, query.ExpandedGroups.Contains(row.Id))).ToArray(), page.Page, page.PageSize, page.TotalCount, page.HasMore);
    }

    /// <inheritdoc />
    public ValueTask<EventStackDetails?> GetDetailsAsync(EventId eventId, CancellationToken cancellationToken = default) => ValueTask.FromResult<EventStackDetails?>(null);
}
