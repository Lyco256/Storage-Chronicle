using System.Collections.ObjectModel;
using StorageChronicle.Contracts;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Projection;
using StorageChronicle.UI.Shared;

namespace StorageChronicle.UI.DiffView;

/// <summary>Headless UI state for the common Tree and Explorer Diff View.</summary>
public sealed class DiffViewModel : IFeatureView
{
    private readonly IDiffProjectionSource projection;
    private readonly IExplorerLauncher explorerLauncher;
    private readonly List<string> navigationHistory = [];
    private readonly Dictionary<string, DiffActivityFrameSnapshot> activityFramesById = new(StringComparer.Ordinal);
    private readonly HashSet<string> pinnedActivityFrames = new(StringComparer.Ordinal);
    private readonly HashSet<string> latestActivityFrameIds = new(StringComparer.Ordinal);
    private int navigationIndex = -1;
    private DiffProjectionQuery? lastQuery;

    /// <summary>Initializes a view over the stable projection contract.</summary>
    public DiffViewModel(IProjectionService projection, IExplorerLauncher? explorerLauncher = null)
        : this(projection is StorageChronicle.Projection.ProjectionService concrete ? new ProjectionServiceDiffSource(concrete) : new ContractDiffProjectionSource(projection), explorerLauncher)
    {
    }

    /// <summary>Initializes a view over an injected rich projection source.</summary>
    public DiffViewModel(IDiffProjectionSource projection, IExplorerLauncher? explorerLauncher = null)
    {
        this.projection = projection ?? throw new ArgumentNullException(nameof(projection));
        this.explorerLauncher = explorerLauncher ?? new UnsupportedExplorerLauncher();
        Tree = new(this);
        Explorer = new(this);
    }

    /// <inheritdoc />
    public string Id => "diff-view";
    /// <inheritdoc />
    public string Title => "Diff View";
    /// <summary>Current diff mode.</summary>
    public DiffMode Mode { get; private set; } = DiffMode.Live;
    /// <summary>Current Explorer presentation.</summary>
    public ExplorerViewMode ViewMode { get; private set; } = ExplorerViewMode.Details;
    /// <summary>Whether Live updates are paused in the UI; Agent recording continues.</summary>
    public bool IsPaused { get; private set; }
    /// <summary>Stable domain rows retained for existing shared-contract consumers.</summary>
    public ObservableCollection<DiffEntry> Rows { get; } = [];
    /// <summary>Rich rows used by both Tree and Explorer.</summary>
    public ObservableCollection<FileDiffProjection> Items { get; } = [];
    /// <summary>Root Tree nodes; descendants are loaded only by expansion.</summary>
    public ObservableCollection<DiffTreeNode> RootNodes { get; } = [];
    /// <summary>Virtualized Explorer row source.</summary>
    public ObservableCollection<DiffExplorerRow> ExplorerRows { get; } = [];
    /// <summary>Currently visible process/location Activity Frames.</summary>
    public ObservableCollection<DiffActivityFrameSnapshot> ActivityFrames { get; } = [];
    /// <summary>Metadata-only event timeline for the selected Activity Frame.</summary>
    public ObservableCollection<DiffActivityFrameEventSnapshot> ActivityFrameEvents { get; } = [];
    /// <summary>Headless Tree renderer.</summary>
    public DiffTreeView Tree { get; }
    /// <summary>Headless Explorer renderer.</summary>
    public DiffExplorerView Explorer { get; }
    /// <summary>Optional two-pane state.</summary>
    public DiffSplitPaneState SplitPanes { get; } = new();
    /// <summary>Replay cursor state.</summary>
    public DiffReplayState Replay { get; } = new();
    /// <summary>Currently selected row.</summary>
    public DiffTreeNode? SelectedNode { get; private set; }
    /// <summary>Currently selected Activity Frame.</summary>
    public DiffActivityFrameSnapshot? SelectedActivityFrame { get; private set; }
    /// <summary>Whether Activity Frames are shown oldest-first rather than newest-first.</summary>
    public bool ActivityFramesAscending { get; private set; }
    /// <summary>Whether another bounded Activity Frame page is available.</summary>
    public bool HasMoreActivityFrames { get; private set; }
    /// <summary>Gets the current lexical path or virtual location shown by Explorer.</summary>
    public string? CurrentPath => SplitPanes.Left.CurrentPath;
    /// <summary>Gets the path components used by the breadcrumb navigation.</summary>
    public IReadOnlyList<DiffPathBreadcrumb> Breadcrumbs => DiffPathNavigation.BuildBreadcrumbs(CurrentPath);
    /// <summary>Gets whether backward navigation is available.</summary>
    public bool CanNavigateBack => navigationIndex > 0;
    /// <summary>Gets whether forward navigation is available.</summary>
    public bool CanNavigateForward => navigationIndex >= 0 && navigationIndex < navigationHistory.Count - 1;

    /// <summary>Loads one common projection for Tree and Explorer.</summary>
    public async ValueTask RefreshAsync(DateTimeOffset? fromUtc, DateTimeOffset toUtc, DiffMode mode, CancellationToken cancellationToken = default)
    {
        var query = new DiffProjectionQuery(fromUtc, toUtc, mode, ProjectionFilter.Empty);
        if (IsPaused && mode == DiffMode.Live) return;
        var result = await projection.GetDiffProjectionAsync(query, cancellationToken).ConfigureAwait(false);
        ApplyProjection(query, result);
    }

    /// <summary>Loads a filtered common projection.</summary>
    public async ValueTask RefreshAsync(DiffProjectionQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (IsPaused && query.Mode == DiffMode.Live) return;
        query = query with { ActivityFramesAscending = ActivityFramesAscending };
        var result = await projection.GetDiffProjectionAsync(query, cancellationToken).ConfigureAwait(false);
        ApplyProjection(query, result);
    }

    /// <summary>Sets one of the eight Explorer layouts.</summary>
    public void SetViewMode(ExplorerViewMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        ViewMode = mode;
    }

    /// <summary>Pauses only Live UI refresh; it does not stop Agent recording.</summary>
    public void PauseLive() => IsPaused = true;

    /// <summary>Resumes Live UI state without performing hidden file-system I/O.</summary>
    public void ResumeLive() => IsPaused = false;

    /// <summary>Resumes and refreshes the last Live query.</summary>
    public async ValueTask ResumeLiveAsync(CancellationToken cancellationToken = default)
    {
        IsPaused = false;
        if (lastQuery is { Mode: DiffMode.Live } query)
        {
            var result = await projection.GetDiffProjectionAsync(query, cancellationToken).ConfigureAwait(false);
            ApplyProjection(query, result);
        }
    }

    /// <summary>Expands one lazy Tree node.</summary>
    public ValueTask ExpandAsync(DiffTreeNode node, CancellationToken cancellationToken = default) => node.ExpandAsync(cancellationToken);

    /// <summary>Selects a node and synchronizes both panes to its path.</summary>
    public void Select(DiffTreeNode? node, bool addToHistory = true)
    {
        SelectedNode = node;
        var path = node?.Projection.NewPath ?? node?.Projection.OldPath;
        SplitPanes.Left.CurrentPath = path;
        if (SplitPanes.IsSplit && !SplitPanes.Right.IsPinned) SplitPanes.Right.CurrentPath = path;
        if (addToHistory && path is not null) AddNavigation(path);
        if (node?.Projection.ReplayTimeline is { Count: > 0 } timeline)
        {
            Replay.CursorUtc = timeline[0].TimeUtc;
            Replay.IsAtPresent = false;
        }
        else
        {
            Replay.CursorUtc = null;
            Replay.IsAtPresent = false;
        }
    }

    /// <summary>Finds and selects a row by path, recording browser-like navigation history.</summary>
    public bool NavigateToPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var row = ExplorerRows.FirstOrDefault(candidate => string.Equals(candidate.Projection.DisplayPath, path, StringComparison.OrdinalIgnoreCase));
        if (row is null) return false;
        Select(NodeFor(row.Projection), addToHistory: true);
        return true;
    }

    /// <summary>Navigates to a rooted path or projected virtual/deleted location without file-system I/O.</summary>
    public bool NavigateToLocation(string path, bool preserveSelection = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var exact = ExplorerRows.FirstOrDefault(candidate => string.Equals(candidate.Projection.DisplayPath, path, StringComparison.OrdinalIgnoreCase));
        if (exact is null && !DiffPathNavigation.TryNormalizeDirectPath(path, out path)) return false;
        var normalized = exact?.Projection.DisplayPath ?? path;
        SplitPanes.Left.CurrentPath = normalized;
        if (SplitPanes.IsSplit && !SplitPanes.Right.IsPinned) SplitPanes.Right.CurrentPath = normalized;
        if (exact is not null) SelectedNode = NodeFor(exact.Projection);
        else if (!preserveSelection) SelectedNode = null;
        AddNavigation(normalized);
        return true;
    }

    /// <summary>Navigates to the parent path lexically without querying the operating system.</summary>
    public bool NavigateUp()
    {
        var parent = DiffPathNavigation.GetParent(CurrentPath);
        return parent is not null && NavigateToLocation(parent);
    }

    /// <summary>Navigates to a breadcrumb by its zero-based index.</summary>
    public bool NavigateToBreadcrumb(int index)
    {
        var breadcrumbs = Breadcrumbs;
        if (index < 0 || index >= breadcrumbs.Count) return false;
        return NavigateToLocation(breadcrumbs[index].Path);
    }

    /// <summary>Moves selection backward through the path history.</summary>
    public bool NavigateBack() => NavigateHistory(-1);

    /// <summary>Moves selection forward through the path history.</summary>
    public bool NavigateForward() => NavigateHistory(1);

    /// <summary>Returns the reciprocal Move row, if both endpoints are projected.</summary>
    public DiffTreeNode? GetMovePartner(DiffTreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var related = node.Projection.RelatedOperationId;
        if (string.IsNullOrWhiteSpace(related)) return null;
        var partner = ExplorerRows.Select(row => row.Projection).FirstOrDefault(item => !ReferenceEquals(item, node.Projection) && string.Equals(item.RelatedOperationId, related, StringComparison.Ordinal));
        return partner is null ? null : NodeFor(partner);
    }

    /// <summary>Selects the reciprocal Move endpoint and synchronizes the split panes.</summary>
    public bool NavigateToMovePartner(DiffTreeNode node)
    {
        var partner = GetMovePartner(node);
        if (partner is null) return false;
        Select(partner);
        return true;
    }

    /// <summary>Attempts to open the selected current item in Explorer.</summary>
    public ValueTask<ExplorerOpenResult> OpenSelectedInExplorerAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedNode is null) return ValueTask.FromResult(ExplorerOpenResult.Rejected("No item is selected."));
        if (!SelectedNode.Projection.CanOpenInExplorer || string.IsNullOrWhiteSpace(SelectedNode.Projection.NewPath))
        {
            return ValueTask.FromResult(ExplorerOpenResult.Rejected(SelectedNode.Projection.OpenDisabledReason ?? "This row cannot be opened in Explorer."));
        }

        return explorerLauncher.OpenAsync(SelectedNode.Projection.NewPath, cancellationToken);
    }

    /// <summary>Sets the replay cursor to a timeline point by index.</summary>
    public bool SetReplayPoint(int index)
    {
        var points = GetSelectedReplayTimeline();
        if (index < 0 || index >= points.Count) return false;
        Replay.CursorUtc = points[index].TimeUtc;
        Replay.IsAtPresent = false;
        RefreshVisibleActivityFrames();
        return true;
    }

    /// <summary>Moves one event at a time through the selected row timeline; returns false at either boundary.</summary>
    public bool StepReplay(int delta)
    {
        if (delta is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(delta), "Replay steps move exactly one event backward or forward.");
        var points = GetSelectedReplayTimeline();
        if (points.Count == 0) return false;
        var current = Replay.CursorUtc is { } cursor ? FindReplayPoint(points, cursor) : -1;
        var next = current < 0 ? (delta > 0 ? 0 : points.Count - 1) : current + delta;
        return SetReplayPoint(next);
    }

    /// <summary>Returns the Replay view to the current projection endpoint.</summary>
    public void ReturnReplayToPresent()
    {
        Replay.CursorUtc = lastQuery?.ToUtc;
        Replay.IsAtPresent = true;
        Replay.Pause();
        RefreshVisibleActivityFrames();
    }

    /// <summary>Sorts Activity Frames by start time, newest-first by default.</summary>
    public async ValueTask SetActivityFrameSortAsync(bool ascending, CancellationToken cancellationToken = default)
    {
        ActivityFramesAscending = ascending;
        if (lastQuery is { } query && query.Mode is DiffMode.Live or DiffMode.Replay)
        {
            var sortedQuery = query with { ActivityFramesPage = 1, ActivityFramesAscending = ascending };
            var result = await projection.GetDiffProjectionAsync(sortedQuery, cancellationToken).ConfigureAwait(false);
            ApplyProjection(sortedQuery, result);
            return;
        }
        RefreshVisibleActivityFrames();
    }

    /// <summary>Loads the next independently paged Activity Frame summary page.</summary>
    public async ValueTask<bool> LoadMoreActivityFramesAsync(CancellationToken cancellationToken = default)
    {
        if (!HasMoreActivityFrames || lastQuery is not { } query) return false;
        var next = query with { ActivityFramesPage = checked(query.ActivityFramesPage + 1) };
        var result = await projection.GetDiffProjectionAsync(next, cancellationToken).ConfigureAwait(false);
        ApplyProjection(next, result);
        return true;
    }

    /// <summary>Selects one displayed Activity Frame.</summary>
    public void SelectActivityFrame(DiffActivityFrameSnapshot? frame)
    {
        SelectedActivityFrame = frame is not null && ActivityFrames.Any(value => value.FrameId == frame.FrameId) ? frame : null;
        ActivityFrameEvents.Clear();
    }

    /// <summary>Toggles the selected frame's pin; pinned frames survive timeout/removal from Live results.</summary>
    public bool ToggleSelectedActivityFramePin()
    {
        if (SelectedActivityFrame is not { } frame) return false;
        if (!pinnedActivityFrames.Add(frame.FrameId)) pinnedActivityFrames.Remove(frame.FrameId);
        if (!pinnedActivityFrames.Contains(frame.FrameId) && !latestActivityFrameIds.Contains(frame.FrameId)) activityFramesById.Remove(frame.FrameId);
        RefreshVisibleActivityFrames();
        return pinnedActivityFrames.Contains(frame.FrameId);
    }

    /// <summary>Returns whether one Activity Frame is pinned.</summary>
    public bool IsActivityFramePinned(string frameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameId);
        return pinnedActivityFrames.Contains(frameId);
    }

    /// <summary>Replaces the selected frame's bounded metadata timeline page.</summary>
    public void SetActivityFrameEvents(IEnumerable<DiffActivityFrameEventSnapshot> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var snapshot = events.ToArray();
        ActivityFrameEvents.Clear();
        foreach (var value in snapshot) ActivityFrameEvents.Add(value);
    }

    /// <summary>Returns the selected Activity Frame timeline, or the selected file timeline when no frame is selected.</summary>
    public IReadOnlyList<ReplayTimelinePoint> GetSelectedReplayTimeline()
    {
        if (SelectedActivityFrame is not null && ActivityFrameEvents.Count > 0)
        {
            return ActivityFrameEvents.Select((value, index) => new ReplayTimelinePoint(
                value.EventId,
                value.TimeUtc,
                index == 0 ? PaneLifecycle.Appeared : PaneLifecycle.Updated,
                ToDiffOperation(value.Operation))).ToArray();
        }

        return SelectedNode?.Projection.ReplayTimeline ?? Array.Empty<ReplayTimelinePoint>();
    }

    private void ApplyProjection(DiffProjectionQuery query, DiffProjectionBundle bundle)
    {
        var result = bundle.Projection;
        Mode = query.Mode;
        lastQuery = query;
        Rows.Clear();
        foreach (var row in result.DomainEntries) Rows.Add(row);
        Items.Clear();
        foreach (var row in result.Items.Concat(result.UnknownLocationItems)) Items.Add(row);
        ExplorerRows.Clear();
        foreach (var item in Items) ExplorerRows.Add(new(RowId(item), item, DiffVisuals.For(item), item.CanOpenInExplorer, item.OpenDisabledReason));
        RootNodes.Clear();
        foreach (var node in DiffTreeBuilder.Build(result.Items, result.UnknownLocationItems)) RootNodes.Add(node);
        if (SelectedNode is not null)
        {
            var selectedId = SelectedNode.NodeId;
            SelectedNode = ExplorerRows.Select(row => NodeFor(row.Projection)).FirstOrDefault(node => node.NodeId == selectedId);
        }
        if (Mode != DiffMode.Replay) Replay.CursorUtc = null;
        else Replay.CursorUtc ??= query.FromUtc ?? query.ToUtc;
        Replay.IsAtPresent = false;
        if (Mode is DiffMode.Live or DiffMode.Replay)
        {
            if (bundle.ActivityFramesPage == 1) latestActivityFrameIds.Clear();
            latestActivityFrameIds.UnionWith(bundle.ActivityFrames.Select(frame => frame.FrameId));
            foreach (var frame in bundle.ActivityFrames) activityFramesById[frame.FrameId] = frame;
            if (bundle.ActivityFramesPage == 1)
            {
                foreach (var id in activityFramesById.Keys.Where(id => !latestActivityFrameIds.Contains(id) && !pinnedActivityFrames.Contains(id)).ToArray())
                    activityFramesById.Remove(id);
            }
        }
        else
        {
            foreach (var id in activityFramesById.Keys.Where(id => !pinnedActivityFrames.Contains(id)).ToArray())
                activityFramesById.Remove(id);
        }
        HasMoreActivityFrames = bundle.HasMoreActivityFrames;
        RefreshVisibleActivityFrames();
    }

    private void RefreshVisibleActivityFrames()
    {
        var cursor = Mode == DiffMode.Replay ? Replay.CursorUtc : null;
        var comparer = ActivityFramesAscending
            ? Comparer<DateTimeOffset>.Default
            : Comparer<DateTimeOffset>.Create((left, right) => right.CompareTo(left));
        var visible = activityFramesById.Values.Where(frame => pinnedActivityFrames.Contains(frame.FrameId) ||
                (Mode != DiffMode.Replay || cursor is null || (frame.StartedUtc <= cursor && cursor < frame.CloseBoundaryUtc)))
            .OrderBy(frame => frame.StartedUtc, comparer)
            .ThenBy(frame => frame.FrameId, StringComparer.Ordinal)
            .ToArray();
        ActivityFrames.Clear();
        foreach (var frame in visible) ActivityFrames.Add(frame);
        if (SelectedActivityFrame is { } selected)
            SelectedActivityFrame = ActivityFrames.FirstOrDefault(frame => frame.FrameId == selected.FrameId);
        if (SelectedActivityFrame is null) ActivityFrameEvents.Clear();
    }

    private DiffTreeNode NodeFor(FileDiffProjection projection)
    {
        var rowId = RowId(projection);
        var known = RootNodes.SelectMany(FlattenLoaded).FirstOrDefault(node => node.NodeId == rowId);
        return known ?? DiffTreeNode.Create(rowId, projection, 0, null);
    }

    private bool NavigateHistory(int delta)
    {
        var next = navigationIndex + delta;
        if (next < 0 || next >= navigationHistory.Count) return false;
        navigationIndex = next;
        var path = navigationHistory[navigationIndex];
        var row = ExplorerRows.FirstOrDefault(candidate => string.Equals(candidate.Projection.DisplayPath, path, StringComparison.OrdinalIgnoreCase));
        SplitPanes.Left.CurrentPath = path;
        if (SplitPanes.IsSplit && !SplitPanes.Right.IsPinned) SplitPanes.Right.CurrentPath = path;
        SelectedNode = row is null ? null : NodeFor(row.Projection);
        return true;
    }

    private void AddNavigation(string path)
    {
        if (navigationIndex >= 0 && string.Equals(navigationHistory[navigationIndex], path, StringComparison.OrdinalIgnoreCase)) return;
        if (navigationIndex < navigationHistory.Count - 1) navigationHistory.RemoveRange(navigationIndex + 1, navigationHistory.Count - navigationIndex - 1);
        navigationHistory.Add(path);
        navigationIndex = navigationHistory.Count - 1;
    }

    private static IEnumerable<DiffTreeNode> FlattenLoaded(DiffTreeNode node)
    {
        yield return node;
        foreach (var child in node.Children.SelectMany(FlattenLoaded)) yield return child;
    }

    private static DiffPrimaryOperation ToDiffOperation(CanonicalOperation operation) => operation switch
    {
        CanonicalOperation.Delete => DiffPrimaryOperation.Delete,
        CanonicalOperation.Recycle => DiffPrimaryOperation.Recycle,
        CanonicalOperation.Restore => DiffPrimaryOperation.Restore,
        CanonicalOperation.Move => DiffPrimaryOperation.MoveTo,
        CanonicalOperation.Rename => DiffPrimaryOperation.Rename,
        CanonicalOperation.Create or CanonicalOperation.DirectoryCreate => DiffPrimaryOperation.Create,
        CanonicalOperation.DataWrite => DiffPrimaryOperation.DataWrite,
        CanonicalOperation.Extend => DiffPrimaryOperation.Resize,
        CanonicalOperation.Truncate => DiffPrimaryOperation.Truncate,
        CanonicalOperation.ShareChanged => DiffPrimaryOperation.Share,
        CanonicalOperation.CloudStateChanged => DiffPrimaryOperation.CloudState,
        _ => DiffPrimaryOperation.MetadataChange
    };

    private static string RowId(FileDiffProjection item) => item.FileId is { } id ? "file:" + id.Value : "path:" + item.DisplayPath;

    private static int FindReplayPoint(IReadOnlyList<ReplayTimelinePoint> points, DateTimeOffset cursor)
    {
        for (var index = 0; index < points.Count; index++)
            if (points[index].TimeUtc == cursor) return index;
        return -1;
    }
}

/// <summary>All Explorer presentation modes required by the product.</summary>
public enum ExplorerViewMode
{
    /// <summary>Displays items using extra-large icons.</summary>
    ExtraLargeIcons,
    /// <summary>Displays items using large icons.</summary>
    LargeIcons,
    /// <summary>Displays items using medium icons.</summary>
    MediumIcons,
    /// <summary>Displays items using small icons.</summary>
    SmallIcons,
    /// <summary>Displays items in a compact list.</summary>
    List,
    /// <summary>Displays items in a details table.</summary>
    Details,
    /// <summary>Displays items as tiles.</summary>
    Tiles,
    /// <summary>Displays items using the content layout.</summary>
    Content
}
