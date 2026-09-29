using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Settings;
using StorageChronicle.UI.Shared;

namespace StorageChronicle.UI.EventStack;

/// <summary>MVVM state for the virtualized Source/Normalized/Grouped Event Stack.</summary>
public sealed class EventStackViewModel : ObservableObject, IFeatureView
{
    private readonly IEventStackProjection projection;
    private readonly HashSet<EventId> expandedGroups = [];
    private EventStackMode mode = EventStackMode.Grouped;
    private EventStackSortDirection sortDirection = EventStackSortDirection.Descending;
    private EventStackFilter filter = EventStackFilter.Empty;
    private EventStackItemViewModel? selectedItem;
    private EventStackDetails? selectedDetails;
    private EventId? selectedEventId;
    private bool isFollowing = true;
    private bool isDetailsOpen;
    private int currentPage = 1;
    private int pageSize = 250;
    private int totalCount;
    private bool hasMore;
    private string? statusMessage;
    private ProcessInstanceId? requestedProcess;
    private SavedEventStackFilter? selectedSavedFilter;
    private readonly IOperationIconResolver iconResolver;
    private readonly IUserSettingsClient? settingsClient;
    private UserSettings? currentUserSettings;
    private string pageSizeInput = "250";
    private string savedFilterName = "Saved filter";
    private const string SavedFilterPrefix = "StorageChronicle.EventStackFilter.v1:";

    /// <summary>Initializes an Event Stack over a platform-neutral projection.</summary>
    public EventStackViewModel(IEventStackProjection projection, IOperationIconResolver? iconResolver = null, IUserSettingsClient? settingsClient = null)
    {
        this.projection = projection ?? throw new ArgumentNullException(nameof(projection));
        this.iconResolver = iconResolver ?? new MaterialOperationIconResolver();
        this.settingsClient = settingsClient;
        LoadPageCommand = new AsyncRelayCommand(() => LoadPageAsync(currentPage, pageSize).AsTask());
        NextPageCommand = new AsyncRelayCommand(() => NextPageAsync().AsTask());
        PreviousPageCommand = new AsyncRelayCommand(() => PreviousPageAsync().AsTask());
        ToggleFollowCommand = new AsyncRelayCommand(ToggleFollowAsync);
        ReturnToCurrentCommand = new AsyncRelayCommand(() => ReturnToCurrentAsync().AsTask());
        SaveFilterCommand = new AsyncRelayCommand(SaveCurrentFilterAsync);
        CloseDetailsCommand = new RelayCommand(() => IsDetailsOpen = false);
        ApplyFilterCommand = new AsyncRelayCommand(() => LoadPageAsync(1, PageSize).AsTask());
        SetModeCommand = new AsyncRelayCommand<EventStackMode>(value => SetModeAsync(value).AsTask());
        SetSortDirectionCommand = new AsyncRelayCommand<EventStackSortDirection>(value => SetSortDirectionAsync(value).AsTask());
        SetPageSizeCommand = new AsyncRelayCommand<int>(value => SetPageSizeAsync(value).AsTask());
        ApplyPageSizeCommand = new AsyncRelayCommand(ApplyPageSizeInputAsync);
        ApplySavedFilterCommand = new AsyncRelayCommand<SavedEventStackFilter>(value => ApplySavedFilterAsync(value!).AsTask());
        NavigateToParentProcessCommand = new RelayCommand(NavigateToParentProcess);
        NavigateToChildProcessCommand = new RelayCommand<ProcessInstanceId>(NavigateToChildProcess);
    }

    /// <summary>Initializes an Event Stack over the shared bounded page source.</summary>
    public EventStackViewModel(IVirtualizedPageSource<EventStackRow> source, IUserSettingsClient? settingsClient = null) : this(new EventStackPageSourceAdapter(source), settingsClient: settingsClient) { }

    /// <inheritdoc />
    public string Id => "event-stack";

    /// <inheritdoc />
    public string Title => "Event Stack";

    /// <summary>All display modes offered by the same query state.</summary>
    public IReadOnlyList<EventStackMode> AvailableModes { get; } = [EventStackMode.Source, EventStackMode.Normalized, EventStackMode.Grouped];

    /// <summary>Both supported orderings.</summary>
    public IReadOnlyList<EventStackSortDirection> AvailableSortDirections { get; } = [EventStackSortDirection.Descending, EventStackSortDirection.Ascending];

    /// <summary>First page-size binding value exposed as an integer for compiled Avalonia bindings.</summary>
    public int PageSize50 => 50;

    /// <summary>Second page-size binding value exposed as an integer for compiled Avalonia bindings.</summary>
    public int PageSize100 => 100;

    /// <summary>Third page-size binding value exposed as an integer for compiled Avalonia bindings.</summary>
    public int PageSize250 => 250;

    /// <summary>Text entry for any supported page size from 50 through 5000.</summary>
    public string PageSizeInput { get => pageSizeInput; set => SetProperty(ref pageSizeInput, value); }

    /// <summary>Name used when saving the current filter.</summary>
    public string SavedFilterName { get => savedFilterName; set => SetProperty(ref savedFilterName, value); }

    /// <summary>Current Source/Normalized/Grouped mode.</summary>
    public EventStackMode Mode { get => mode; private set => SetProperty(ref mode, value); }

    /// <summary>Current newest-first or oldest-first ordering.</summary>
    public EventStackSortDirection SortDirection { get => sortDirection; private set => SetProperty(ref sortDirection, value); }

    /// <summary>Search text shared across all modes.</summary>
    public string SearchText
    {
        get => filter.SearchText;
        set
        {
            if (string.Equals(filter.SearchText, value, StringComparison.Ordinal)) return;
            filter = filter with { SearchText = value ?? string.Empty };
            OnPropertyChanged();
        }
    }

    /// <summary>Current quality filter shown in the filter bar.</summary>
    public EventQuality? QualityFilter => filter.Quality;

    /// <summary>Current origin filter shown in the filter bar.</summary>
    public EventOrigin? OriginFilter => filter.Origin;

    /// <summary>Current operation filter shown in the filter bar.</summary>
    public CanonicalOperation? OperationFilter => filter.Operation;

    /// <summary>Whether the view follows newly projected rows.</summary>
    public bool IsFollowing { get => isFollowing; private set => SetProperty(ref isFollowing, value); }

    /// <summary>Whether the selected detail panel is visible.</summary>
    public bool IsDetailsOpen { get => isDetailsOpen; private set => SetProperty(ref isDetailsOpen, value); }

    /// <summary>Current one-based page.</summary>
    public int CurrentPage { get => currentPage; private set { if (SetProperty(ref currentPage, value)) OnPropertyChanged(nameof(CanGoPrevious)); } }

    /// <summary>Maximum number of rows materialized for one page.</summary>
    public int PageSize { get => pageSize; private set => SetProperty(ref pageSize, value); }

    /// <summary>Total rows reported by the projection.</summary>
    public int TotalCount { get => totalCount; private set => SetProperty(ref totalCount, value); }

    /// <summary>Whether a following page exists.</summary>
    public bool HasMore { get => hasMore; private set => SetProperty(ref hasMore, value); }

    /// <summary>Whether a previous page is available.</summary>
    public bool CanGoPrevious => CurrentPage > 1;

    /// <summary>Rows materialized for the current page only; this is the virtualization boundary.</summary>
    public ObservableCollection<EventStackItemViewModel> Rows { get; } = [];

    /// <summary>Saved filter choices owned by this UI instance.</summary>
    public ObservableCollection<SavedEventStackFilter> SavedFilters { get; } = [];

    /// <summary>Currently selected row.</summary>
    public EventStackItemViewModel? SelectedItem { get => selectedItem; private set => SetProperty(ref selectedItem, value); }

    /// <summary>Selected row details with source quality and process navigation.</summary>
    public EventStackDetails? SelectedDetails
    {
        get => selectedDetails;
        private set
        {
            if (!SetProperty(ref selectedDetails, value)) return;
            ChildProcessLinks.Clear();
            if (value is not null)
            {
                foreach (var process in value.ChildProcesses) ChildProcessLinks.Add(new ProcessNavigationViewModel(process, NavigateToChildProcess));
            }
        }
    }

    /// <summary>Saved filter selected in the filter bar.</summary>
    public SavedEventStackFilter? SelectedSavedFilter { get => selectedSavedFilter; set => SetProperty(ref selectedSavedFilter, value); }

    /// <summary>Child process links shown in the detail panel.</summary>
    public ObservableCollection<ProcessNavigationViewModel> ChildProcessLinks { get; } = [];

    /// <summary>Process requested by a detail-panel navigation action.</summary>
    public ProcessInstanceId? RequestedProcess { get => requestedProcess; private set => SetProperty(ref requestedProcess, value); }

    /// <summary>Human-readable asynchronous status.</summary>
    public string? StatusMessage { get => statusMessage; private set => SetProperty(ref statusMessage, value); }

    /// <summary>Sets a status reported by the view boundary, such as an initial IPC failure.</summary>
    public void SetStatusMessage(string value) => StatusMessage = value;

    /// <summary>Loads or reloads one bounded page.</summary>
    public async ValueTask LoadPageAsync(int requestedPage = 1, int requestedPageSize = 250, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requestedPage, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(requestedPageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(requestedPageSize, 5000);
        PageSize = requestedPageSize;
        var query = new EventStackQuery(Mode, requestedPage, PageSize, SortDirection, filter, expandedGroups);
        var page = await projection.GetPageAsync(query, cancellationToken).ConfigureAwait(false);
        CurrentPage = page.Page;
        TotalCount = page.TotalCount;
        HasMore = page.HasMore;
        var priorSelection = selectedEventId;
        Rows.Clear();
        foreach (var item in page.Items) Rows.Add(new EventStackItemViewModel(item, ToggleExpansion, iconResolver));
        SelectedItem = priorSelection is null ? null : Rows.FirstOrDefault(row => row.EventId == priorSelection.Value);
        if (SelectedItem is null && priorSelection is null && Rows.Count > 0) SelectedItem = Rows[0];
        StatusMessage = $"Page {CurrentPage} · {Rows.Count} of {TotalCount}";
    }

    /// <summary>Loads persisted user preferences before the first page is displayed.</summary>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (settingsClient is not null)
        {
            try
            {
                currentUserSettings = await settingsClient.LoadUserSettingsAsync(cancellationToken).ConfigureAwait(false);
                PageSize = currentUserSettings.EventStackPageSize;
                PageSizeInput = PageSize.ToString(CultureInfo.InvariantCulture);
                SortDirection = currentUserSettings.EventStackSort == EventStackSortOrder.OldestFirst
                    ? EventStackSortDirection.Ascending
                    : EventStackSortDirection.Descending;
                Mode = currentUserSettings.InitialEventStackMode switch
                {
                    EventStackInitialMode.Source => EventStackMode.Source,
                    EventStackInitialMode.Normalized => EventStackMode.Normalized,
                    _ => EventStackMode.Grouped
                };
                SavedFilters.Clear();
                foreach (var encoded in currentUserSettings.SavedFilters)
                {
                    SavedFilters.Add(DecodeSavedFilter(encoded));
                }
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                StatusMessage = $"User preferences could not be loaded: {exception.Message}";
            }
        }

        await LoadPageAsync(1, PageSize, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Switches display mode without changing filter, sort, selection, or expansion state.</summary>
    public async ValueTask SetModeAsync(EventStackMode value, CancellationToken cancellationToken = default)
    {
        Mode = value;
        await LoadPageAsync(CurrentPage, PageSize, cancellationToken).ConfigureAwait(false);
        await PersistUserSettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Changes ordering and reloads from the first page.</summary>
    public ValueTask SetSortDirectionAsync(EventStackSortDirection value, CancellationToken cancellationToken = default)
    {
        SortDirection = value;
        return SetSortAndPersistAsync(cancellationToken);
    }

    /// <summary>Changes the maximum rows materialized per page.</summary>
    public ValueTask SetPageSizeAsync(int value, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 50);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 5000);
        return SetPageSizeAndPersistAsync(value, cancellationToken);
    }

    /// <summary>Applies text and optional semantic filters to every display mode.</summary>
    public ValueTask ApplyFilterAsync(string? searchText, EventQuality? quality = null, EventOrigin? origin = null, CanonicalOperation? operation = null, CancellationToken cancellationToken = default)
    {
        filter = new EventStackFilter(searchText?.Trim() ?? string.Empty, quality, origin, operation);
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(QualityFilter));
        OnPropertyChanged(nameof(OriginFilter));
        OnPropertyChanged(nameof(OperationFilter));
        return LoadPageAsync(1, PageSize, cancellationToken);
    }

    /// <summary>Saves the current filter state for reuse.</summary>
    public void SaveCurrentFilter(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A filter name is required.", nameof(name));
        SavedFilters.Add(new SavedEventStackFilter(name.Trim(), filter));
    }

    private async Task SaveCurrentFilterAsync()
    {
        SaveCurrentFilter(SavedFilterName);
        await PersistUserSettingsAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ApplyPageSizeInputAsync()
    {
        if (!int.TryParse(PageSizeInput, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 50 or > 5000)
        {
            StatusMessage = "Page size must be an integer from 50 through 5000.";
            return;
        }

        await SetPageSizeAsync(value).ConfigureAwait(false);
    }

    private async ValueTask SetPageSizeAndPersistAsync(int value, CancellationToken cancellationToken)
    {
        await LoadPageAsync(1, value, cancellationToken).ConfigureAwait(false);
        PageSizeInput = value.ToString(CultureInfo.InvariantCulture);
        await PersistUserSettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SetSortAndPersistAsync(CancellationToken cancellationToken)
    {
        await LoadPageAsync(1, PageSize, cancellationToken).ConfigureAwait(false);
        await PersistUserSettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PersistUserSettingsAsync(CancellationToken cancellationToken)
    {
        if (settingsClient is null || currentUserSettings is null) return;
        var updated = currentUserSettings with
        {
            EventStackPageSize = PageSize,
            EventStackSort = SortDirection == EventStackSortDirection.Ascending ? EventStackSortOrder.OldestFirst : EventStackSortOrder.NewestFirst,
            InitialEventStackMode = Mode switch
            {
                EventStackMode.Source => EventStackInitialMode.Source,
                EventStackMode.Normalized => EventStackInitialMode.Normalized,
                _ => EventStackInitialMode.Grouped
            },
            SavedFilters = SavedFilters.Select(EncodeSavedFilter).ToArray()
        };
        var result = await settingsClient.ApplyUserSettingsAsync(updated, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            currentUserSettings = updated;
            StatusMessage = "Event Stack preferences saved.";
        }
        else
        {
            StatusMessage = $"Event Stack preferences were not saved: {result.Error ?? "Agent rejected the update."}";
        }
    }

    private static string EncodeSavedFilter(SavedEventStackFilter saved)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(saved);
        return SavedFilterPrefix + Convert.ToBase64String(bytes);
    }

    private static SavedEventStackFilter DecodeSavedFilter(string value)
    {
        if (value.StartsWith(SavedFilterPrefix, StringComparison.Ordinal))
        {
            try
            {
                var bytes = Convert.FromBase64String(value[SavedFilterPrefix.Length..]);
                return JsonSerializer.Deserialize<SavedEventStackFilter>(bytes)
                    ?? new SavedEventStackFilter(value, new EventStackFilter(value, null, null, null));
            }
            catch (FormatException) { }
            catch (JsonException) { }
        }

        return new SavedEventStackFilter(value, new EventStackFilter(value, null, null, null));
    }

    /// <summary>Applies one saved filter and returns to its first page.</summary>
    public ValueTask ApplySavedFilterAsync(SavedEventStackFilter saved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(saved);
        filter = saved.Filter;
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(QualityFilter));
        OnPropertyChanged(nameof(OriginFilter));
        OnPropertyChanged(nameof(OperationFilter));
        return LoadPageAsync(1, PageSize, cancellationToken);
    }

    /// <summary>Moves to the next page without materializing rows outside that page.</summary>
    public ValueTask NextPageAsync()
    {
        return HasMore ? LoadPageAsync(CurrentPage + 1, PageSize) : ValueTask.CompletedTask;
    }

    /// <summary>Moves to the previous page.</summary>
    public ValueTask PreviousPageAsync() => CanGoPrevious ? LoadPageAsync(CurrentPage - 1, PageSize) : ValueTask.CompletedTask;

    /// <summary>Stops automatic live-tail updates when the user scrolls into history.</summary>
    public void StopFollowing() => IsFollowing = false;

    /// <summary>Marks the view as historical after a scroll gesture.</summary>
    public void OnScrolledAwayFromTail() => StopFollowing();

    /// <summary>Returns to the live page and resumes automatic following.</summary>
    public ValueTask ReturnToCurrentAsync(CancellationToken cancellationToken = default)
    {
        IsFollowing = true;
        return LoadPageAsync(1, PageSize, cancellationToken);
    }

    /// <summary>Refreshes the live page only when follow mode is active.</summary>
    public ValueTask OnLiveUpdateAsync(CancellationToken cancellationToken = default) => IsFollowing ? LoadPageAsync(1, PageSize, cancellationToken) : ValueTask.CompletedTask;

    /// <summary>Selects a row and asynchronously loads its details.</summary>
    public async ValueTask SelectAsync(EventStackItemViewModel? item, CancellationToken cancellationToken = default)
    {
        SelectedItem = item;
        selectedEventId = item?.EventId;
        SelectedDetails = null;
        IsDetailsOpen = item is not null;
        if (item is not null) SelectedDetails = await projection.GetDetailsAsync(item.EventId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Expands or collapses a grouped row without breaking the page boundary.</summary>
    public void ToggleExpansion(EventStackItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.HasChildren) return;
        if (expandedGroups.Remove(item.EventId)) item.IsExpanded = false;
        else { expandedGroups.Add(item.EventId); item.IsExpanded = true; }
    }

    /// <summary>Moves the detail panel focus request to a parent process.</summary>
    public void NavigateToParentProcess() => NavigateToProcess(SelectedDetails?.ParentProcess);

    /// <summary>Moves the detail panel focus request to a child process.</summary>
    public void NavigateToChildProcess(ProcessInstanceId process) => NavigateToProcess(process);

    /// <summary>Raises a process navigation request without reading process state.</summary>
    public void NavigateToProcess(ProcessInstanceId? process)
    {
        RequestedProcess = process;
        if (process is not null) StatusMessage = $"Process selected: {process}";
    }

    /// <summary>Handles keyboard actions in a platform-neutral way for the view code-behind.</summary>
    public async ValueTask HandleKeyAsync(EventStackKey key, CancellationToken cancellationToken = default)
    {
        switch (key)
        {
            case EventStackKey.Up: SelectSibling(-1); break;
            case EventStackKey.Down: SelectSibling(1); break;
            case EventStackKey.Enter: if (SelectedItem is not null) { ToggleExpansion(SelectedItem); await SelectAsync(SelectedItem, cancellationToken).ConfigureAwait(false); } break;
            case EventStackKey.Right: if (SelectedItem is not null && !SelectedItem.IsExpanded) ToggleExpansion(SelectedItem); break;
            case EventStackKey.Left: if (SelectedItem is not null && SelectedItem.IsExpanded) ToggleExpansion(SelectedItem); break;
            case EventStackKey.PageUp: await PreviousPageAsync().ConfigureAwait(false); break;
            case EventStackKey.PageDown: await NextPageAsync().ConfigureAwait(false); break;
            case EventStackKey.Home: await LoadPageAsync(1, PageSize, cancellationToken).ConfigureAwait(false); break;
            case EventStackKey.End: while (HasMore) await NextPageAsync().ConfigureAwait(false); break;
            case EventStackKey.Escape: IsDetailsOpen = false; break;
        }
    }

    /// <summary>Command bound to the refresh button.</summary>
    public IAsyncRelayCommand LoadPageCommand { get; }

    /// <summary>Command bound to the next-page button.</summary>
    public IAsyncRelayCommand NextPageCommand { get; }

    /// <summary>Command bound to the previous-page button.</summary>
    public IAsyncRelayCommand PreviousPageCommand { get; }

    /// <summary>Command bound to the live-follow button.</summary>
    public IAsyncRelayCommand ToggleFollowCommand { get; }

    /// <summary>Command bound to the return-to-current button.</summary>
    public IAsyncRelayCommand ReturnToCurrentCommand { get; }

    /// <summary>Command that applies the current search and filter controls.</summary>
    public IAsyncRelayCommand ApplyFilterCommand { get; }

    /// <summary>Command used by the three mode buttons.</summary>
    public IAsyncRelayCommand<EventStackMode> SetModeCommand { get; }

    /// <summary>Command used by the ascending/descending buttons.</summary>
    public IAsyncRelayCommand<EventStackSortDirection> SetSortDirectionCommand { get; }

    /// <summary>Command used by page-size controls.</summary>
    public IAsyncRelayCommand<int> SetPageSizeCommand { get; }

    /// <summary>Applies the page size entered in the UI.</summary>
    public IAsyncRelayCommand ApplyPageSizeCommand { get; }

    /// <summary>Command that applies the selected saved filter.</summary>
    public IAsyncRelayCommand<SavedEventStackFilter> ApplySavedFilterCommand { get; }

    /// <summary>Command that moves to the recorded parent process.</summary>
    public IRelayCommand NavigateToParentProcessCommand { get; }

    /// <summary>Command that moves to a recorded child process.</summary>
    public IRelayCommand<ProcessInstanceId> NavigateToChildProcessCommand { get; }

    /// <summary>Command bound to the save-filter button.</summary>
    public IAsyncRelayCommand SaveFilterCommand { get; }

    /// <summary>Command bound to the close-details button.</summary>
    public IRelayCommand CloseDetailsCommand { get; }

    private async Task ToggleFollowAsync()
    {
        if (IsFollowing) StopFollowing();
        else await ReturnToCurrentAsync().ConfigureAwait(false);
    }

    private void SelectSibling(int direction)
    {
        if (Rows.Count == 0) return;
        var index = SelectedItem is null ? 0 : Rows.IndexOf(SelectedItem) + direction;
        index = Math.Clamp(index, 0, Rows.Count - 1);
        SelectedItem = Rows[index];
        selectedEventId = SelectedItem.EventId;
        IsDetailsOpen = true;
    }
}

/// <summary>Bindable page row that exposes quality, process, expansion, and accessibility wording.</summary>
public sealed class EventStackItemViewModel : ObservableObject
{
    private readonly Action<EventStackItemViewModel> toggleExpansion;
    private bool isExpanded;

    internal EventStackItemViewModel(EventStackItem item, Action<EventStackItemViewModel> toggleExpansion, IOperationIconResolver iconResolver)
    {
        Row = item.Row;
        Children = new ObservableCollection<EventStackChildViewModel>((item.NestedChildren is { Count: > 0 }
            ? item.NestedChildren
            : item.Children.Select(child => new EventStackItem(child, Array.Empty<EventStackRow>(), null, null, false, false)))
            .Select(child => new EventStackChildViewModel(child)));
        FileSummary = item.FileSummary ?? item.Row.Summary;
        ProcessDisplay = item.ProcessName ?? "Unknown process";
        IsGroup = item.IsGroup;
        isExpanded = item.IsExpanded;
        this.toggleExpansion = toggleExpansion;
        ToggleExpansionCommand = new RelayCommand(() => this.toggleExpansion(this));
        IconKey = iconResolver.Resolve(OperationIconMeaningFor(item.Row.Operation));
    }

    /// <summary>Underlying immutable projection row.</summary>
    public EventStackRow Row { get; }

    /// <summary>Expansion children returned in the same page as the group.</summary>
    public ObservableCollection<EventStackChildViewModel> Children { get; }

    /// <summary>File summary suitable for a grouped row.</summary>
    public string FileSummary { get; }

    /// <summary>Recorded process name or explicit Unknown process wording.</summary>
    public string ProcessDisplay { get; }

    /// <summary>Material Icons identifier resolved from the semantic operation.</summary>
    public string IconKey { get; }

    /// <summary>Whether this row represents a grouped operation.</summary>
    public bool IsGroup { get; }

    /// <summary>Whether bounded children are visible.</summary>
    public bool IsExpanded { get => isExpanded; internal set => SetProperty(ref isExpanded, value); }

    /// <summary>Whether this row can expand.</summary>
    public bool HasChildren => Children.Count > 0;

    /// <summary>Stable event id.</summary>
    public EventId EventId => Row.Id;

    /// <summary>Operation and summary text.</summary>
    public string DisplaySummary => $"{Row.Operation}: {Row.Summary}";

    /// <summary>Quality wording with Exact/Correlated/Unknown made explicit.</summary>
    public string QualityDisplay => Row.Quality switch
    {
        EventQuality.Exact => "Exact",
        EventQuality.Correlated => "Correlated",
        EventQuality.Unknown => "Unknown",
        EventQuality.ExistenceOnly => "Existence only",
        EventQuality.UnverifiedGap => "Unverified gap",
        EventQuality.Reconciled => "Reconciliation",
        _ => Row.Quality.ToString()
    };

    /// <summary>Meaningful accessibility name and tooltip content.</summary>
    public string AccessibleDescription => $"{DisplaySummary}; {QualityDisplay}; {ProcessDisplay}; {Row.TimeUtc.LocalDateTime:g}";

    /// <summary>Command bound to the expand/collapse affordance.</summary>
    public IRelayCommand ToggleExpansionCommand { get; }

    private static OperationIconMeaning OperationIconMeaningFor(CanonicalOperation operation) => operation switch
    {
        CanonicalOperation.Create or CanonicalOperation.DirectoryCreate => OperationIconMeaning.Created,
        CanonicalOperation.DataWrite or CanonicalOperation.Extend or CanonicalOperation.Truncate or CanonicalOperation.MetadataChanged => OperationIconMeaning.Edited,
        CanonicalOperation.Move => OperationIconMeaning.Moved,
        CanonicalOperation.Rename => OperationIconMeaning.Renamed,
        CanonicalOperation.Delete => OperationIconMeaning.Deleted,
        CanonicalOperation.Recycle => OperationIconMeaning.Recycled,
        CanonicalOperation.Restore => OperationIconMeaning.Restored,
        CanonicalOperation.ShareChanged or CanonicalOperation.CloudStateChanged => OperationIconMeaning.Shared,
        CanonicalOperation.ReconciliationDiscovered => OperationIconMeaning.Reconciled,
        _ => OperationIconMeaning.Unknown
    };
}

/// <summary>Bindable child row for a grouped operation.</summary>
public sealed class EventStackChildViewModel : ObservableObject
{
    private bool isExpanded;

    internal EventStackChildViewModel(EventStackItem item)
    {
        Row = item.Row;
        Children = new ObservableCollection<EventStackChildViewModel>((item.NestedChildren ?? Array.Empty<EventStackItem>()).Select(child => new EventStackChildViewModel(child)));
        isExpanded = item.IsExpanded;
        ToggleExpansionCommand = new RelayCommand(() => IsExpanded = !IsExpanded, () => HasChildren);
    }

    /// <summary>Underlying child row.</summary>
    public EventStackRow Row { get; }

    /// <summary>Nested file, normalized, and source rows retained by the recursive projection.</summary>
    public ObservableCollection<EventStackChildViewModel> Children { get; }

    /// <summary>Whether this child has another expandable level.</summary>
    public bool HasChildren => Children.Count > 0;

    /// <summary>Whether nested child rows are visible.</summary>
    public bool IsExpanded
    {
        get => isExpanded;
        private set
        {
            if (!SetProperty(ref isExpanded, value)) return;
            ToggleExpansionCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Command bound to the child expand/collapse affordance.</summary>
    public RelayCommand ToggleExpansionCommand { get; }

    /// <summary>Child operation summary.</summary>
    public string DisplaySummary => $"{Row.Operation}: {Row.Summary}";

    /// <summary>Child quality wording.</summary>
    public string QualityDisplay => Row.Quality.ToString();
}

/// <summary>Bindable process navigation link used by the detail panel.</summary>
public sealed class ProcessNavigationViewModel
{
    private readonly Action<ProcessInstanceId> navigate;

    internal ProcessNavigationViewModel(ProcessInstanceId process, Action<ProcessInstanceId> navigate)
    {
        Process = process;
        this.navigate = navigate;
        NavigateCommand = new RelayCommand(() => this.navigate(Process));
    }

    /// <summary>Process identity recorded in event details.</summary>
    public ProcessInstanceId Process { get; }

    /// <summary>Stable command for keyboard and pointer activation.</summary>
    public IRelayCommand NavigateCommand { get; }
}
