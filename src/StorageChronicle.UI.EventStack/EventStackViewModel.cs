using System.Collections.ObjectModel;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.UI.Shared;

namespace StorageChronicle.UI.EventStack;

/// <summary>UI-only Event Stack state with paged rows and live-follow semantics.</summary>
public sealed class EventStackViewModel : IFeatureView
{
    private readonly IVirtualizedPageSource<EventStackRow> source;
    private int page = 1;
    private int pageSize = 250;

    /// <summary>Initializes an Event Stack against a fake or IPC-backed page source.</summary>
    public EventStackViewModel(IVirtualizedPageSource<EventStackRow> source) => this.source = source ?? throw new ArgumentNullException(nameof(source));
    /// <inheritdoc />
    public string Id => "event-stack";
    /// <inheritdoc />
    public string Title => "Event Stack";
    /// <summary>Current display mode.</summary>
    public EventStackMode Mode { get; private set; } = EventStackMode.Grouped;
    /// <summary>Whether new live rows follow the tail.</summary>
    public bool IsFollowing { get; private set; } = true;
    /// <summary>Current page rows; only the requested page is materialized.</summary>
    public ObservableCollection<EventStackRow> Rows { get; } = [];
    /// <summary>Currently selected event.</summary>
    public EventStackRow? Selected { get; private set; }

    /// <summary>Loads one bounded page.</summary>
    public async ValueTask LoadPageAsync(int requestedPage, int requestedPageSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requestedPage, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(requestedPageSize, 50);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(requestedPageSize, 5000);
        page = requestedPage;
        pageSize = requestedPageSize;
        var result = await source.GetPageAsync(page, pageSize, cancellationToken).ConfigureAwait(false);
        Rows.Clear();
        foreach (var row in result.Items) Rows.Add(row);
        if (Selected is not null) Selected = Rows.FirstOrDefault(value => value.Id == Selected.Id);
    }

    /// <summary>Switches Source/Normalized/Grouped without clearing selection semantics.</summary>
    public async ValueTask SetModeAsync(EventStackMode mode, CancellationToken cancellationToken = default) { Mode = mode; await LoadPageAsync(page, pageSize, cancellationToken).ConfigureAwait(false); }
    /// <summary>Stops live tail-follow when the user scrolls into history.</summary>
    public void StopFollowing() => IsFollowing = false;
    /// <summary>Resumes live tail-follow and requests the current page.</summary>
    public ValueTask ReturnToCurrentAsync(CancellationToken cancellationToken = default) { IsFollowing = true; return LoadPageAsync(1, pageSize, cancellationToken); }
    /// <summary>Preserves the row selection across a mode or page refresh.</summary>
    public void Select(EventStackRow? row) => Selected = row;
}
