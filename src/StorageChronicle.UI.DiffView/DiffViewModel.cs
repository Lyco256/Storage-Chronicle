using System.Collections.ObjectModel;
using StorageChronicle.Contracts;
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
        if (node?.Projection.ReplayTimeline is { Count: > 0 } timeline) Replay.CursorUtc = timeline[0].TimeUtc;
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
        var points = SelectedNode?.Projection.ReplayTimeline ?? Array.Empty<ReplayTimelinePoint>();
        if (index < 0 || index >= points.Count) return false;
        Replay.CursorUtc = points[index].TimeUtc;
        return true;
    }

    /// <summary>Returns the Replay view to the current projection endpoint.</summary>
    public void ReturnReplayToPresent() => Replay.CursorUtc = lastQuery?.ToUtc;

    private void ApplyProjection(DiffProjectionQuery query, DiffProjection result)
    {
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
        if (row is not null) Select(NodeFor(row.Projection), addToHistory: false);
        return row is not null;
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

    private static string RowId(FileDiffProjection item) => item.FileId is { } id ? "file:" + id.Value : "path:" + item.DisplayPath;
}

/// <summary>All Explorer presentation modes required by the product.</summary>
public enum ExplorerViewMode { ExtraLargeIcons, LargeIcons, MediumIcons, SmallIcons, List, Details, Tiles, Content }
