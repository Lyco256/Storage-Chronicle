using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using StorageChronicle.Contracts;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Projection;
using StorageChronicle.UI.DiffView;
using StorageChronicle.UI.Shared;
using System.Collections.ObjectModel;

namespace StorageChronicle.UI.Desktop;

/// <summary>Desktop host for the shared Tree/Explorer Diff View and its four time modes.</summary>
public sealed class DiffProjectionPanel : UserControl
{
    private readonly DiffViewModel viewModel;
    private readonly ObservableCollection<string> treeRows = [];
    private readonly ObservableCollection<string> explorerRows = [];
    private readonly ListBox treeList;
    private readonly ListBox explorerList;
    private readonly TextBlock status;
    private readonly TextBox filter;
    private readonly Grid resultGrid;
    private readonly TextBlock leftPanePath;
    private readonly TextBlock rightPanePath;
    private readonly Button openButton;
        private readonly Button replayPlayButton;

    /// <summary>Creates a Diff View backed by the Agent projection pipe.</summary>
    public DiffProjectionPanel(IProjectionService projection)
    {
        viewModel = projection is AgentPipeProjectionClient agent
            ? new DiffViewModel(new AgentDiffProjectionSource(agent))
            : new DiffViewModel(projection);

        treeList = new ListBox { ItemsSource = treeRows, [AutomationProperties.NameProperty] = "Diff View tree" };
        explorerList = new ListBox { ItemsSource = explorerRows, IsVisible = false, [AutomationProperties.NameProperty] = "Diff View Explorer" };
        treeList.SelectionChanged += (_, _) => SelectTreeRow();
        explorerList.SelectionChanged += (_, _) => SelectExplorerRow();
        status = new TextBlock { Text = "Diff View: Agent connection is idle." };
        filter = new TextBox { PlaceholderText = "Name, path, or operation", [AutomationProperties.NameProperty] = "Diff filter" };

        var expandTree = new Button { Content = "Expand/collapse tree", [AutomationProperties.NameProperty] = "Expand or collapse selected diff tree row" };
        expandTree.Click += async (_, _) => await ToggleSelectedTreeNodeAsync().ConfigureAwait(true);

        var refresh = new Button { Content = "Refresh", [AutomationProperties.NameProperty] = "Refresh diff" };
        refresh.Click += async (_, _) => await RefreshAsync(viewModel.Mode).ConfigureAwait(true);
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var mode in Enum.GetValues<DiffMode>())
        {
            var button = new Button { Content = mode.ToString(), [AutomationProperties.NameProperty] = $"Diff mode {mode}" };
            button.Click += async (_, _) => await RefreshAsync(mode).ConfigureAwait(true);
            modes.Children.Add(button);
        }

        var treeButton = new Button { Content = "Tree", [AutomationProperties.NameProperty] = "Show diff tree" };
        treeButton.Click += (_, _) => ShowTree();
        var explorerButton = new Button { Content = "Explorer", [AutomationProperties.NameProperty] = "Show diff Explorer" };
        explorerButton.Click += (_, _) => ShowExplorer();
        var presentations = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { treeButton, explorerButton } };

        var explorerModes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var mode in DiffExplorerView.SupportedModes)
        {
            var button = new Button { Content = mode.ToString(), [AutomationProperties.NameProperty] = $"Explorer layout {mode}" };
            button.Click += (_, _) => { viewModel.SetViewMode(mode); RenderRows(); };
            explorerModes.Children.Add(button);
        }

        var back = new Button { Content = "Back", [AutomationProperties.NameProperty] = "Diff navigation back" };
        back.Click += (_, _) => { viewModel.NavigateBack(); UpdatePaneState(); };
        var forward = new Button { Content = "Forward", [AutomationProperties.NameProperty] = "Diff navigation forward" };
        forward.Click += (_, _) => { viewModel.NavigateForward(); UpdatePaneState(); };
        openButton = new Button { Content = "Open in Explorer", [AutomationProperties.NameProperty] = "Open selected diff item in Explorer" };
        openButton.Click += async (_, _) => await OpenSelectedAsync().ConfigureAwait(true);
        var split = new ToggleButton { Content = "Split panes", [AutomationProperties.NameProperty] = "Toggle diff split panes" };
        split.IsCheckedChanged += (_, _) => { viewModel.SplitPanes.SetSplit(split.IsChecked == true); UpdateSplitLayout(); UpdatePaneState(); };
        replayPlayButton = new Button { Content = "Play replay", [AutomationProperties.NameProperty] = "Play or pause diff replay" };
        replayPlayButton.Click += (_, _) => ToggleReplay();
        var present = new Button { Content = "Present", [AutomationProperties.NameProperty] = "Return replay to present" };
        present.Click += (_, _) => { viewModel.ReturnReplayToPresent(); UpdatePaneState(); };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { filter, refresh, presentations, expandTree, back, forward, openButton, split, replayPlayButton, present } };

        leftPanePath = new TextBlock { Text = "Left pane: no selection", [AutomationProperties.NameProperty] = "Diff left pane" };
        rightPanePath = new TextBlock { Text = "Right pane: no selection", [AutomationProperties.NameProperty] = "Diff right pane" };
        var leftPane = new StackPanel { Spacing = 4, Children = { leftPanePath, treeList, explorerList } };
        var rightPane = new StackPanel { Spacing = 4, Children = { rightPanePath, new TextBlock { Text = "Replay and comparison state is derived from the selected projection." } } };
        resultGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1* 1*"),
            Children =
            {
                new Border { Child = leftPane },
                new Border { Child = rightPane, [Grid.ColumnProperty] = 1 }
            }
        };
        UpdateSplitLayout();

        Content = new DockPanel
        {
            Margin = new Avalonia.Thickness(16),
            Children =
            {
                new StackPanel { Orientation = Orientation.Vertical, Spacing = 8, Children = { modes, toolbar, explorerModes, status, resultGrid } }
            }
        };
        AttachedToVisualTree += async (_, _) => await RefreshAsync(DiffMode.Live).ConfigureAwait(true);
    }

    private async Task RefreshAsync(DiffMode mode)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            var projectionFilter = string.IsNullOrWhiteSpace(filter.Text)
                ? ProjectionFilter.Empty
                : new ProjectionFilter(Any: new[] { new FilterTerm(FilterField.Name, filter.Text.Trim()) });
            await viewModel.RefreshAsync(new DiffProjectionQuery(mode == DiffMode.PointInTime ? null : now.AddHours(-1), now, mode, projectionFilter)).ConfigureAwait(true);
            RenderRows();
            status.Text = $"{mode}: {viewModel.Items.Count} rows; Explorer layout: {viewModel.ViewMode}";
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            status.Text = $"Agent unavailable: {exception.Message}";
        }
    }

    private void RenderRows()
    {
        treeRows.Clear();
        foreach (var row in viewModel.Tree.GetVisibleRows())
        {
            var secondary = row.Visuals.Secondary.Count == 0 ? string.Empty : $" +{row.Visuals.Secondary.Count} markers";
            treeRows.Add($"{new string(' ', row.Depth * 2)}{row.Visuals.Primary.IconKey}{secondary} {row.Node.Projection.DisplayName} ({row.Node.Projection.DescendantCount})");
        }

        explorerRows.Clear();
        foreach (var row in viewModel.Explorer.GetPage(1, 250))
        {
            var openState = row.CanOpenInExplorer ? string.Empty : $" [{row.OpenDisabledReason}]";
            explorerRows.Add($"{row.Visuals.Primary.IconKey} {viewModel.ViewMode}: {row.Projection.DisplayName} — {row.Projection.DisplayPath}{openState}");
        }
        UpdatePaneState();
    }

    private void ShowTree()
    {
        treeList.IsVisible = true;
        explorerList.IsVisible = false;
    }

    private void ShowExplorer()
    {
        treeList.IsVisible = false;
        explorerList.IsVisible = true;
    }

    private void SelectTreeRow()
    {
        var rows = viewModel.Tree.GetVisibleRows();
        if (treeList.SelectedIndex is < 0 || treeList.SelectedIndex >= rows.Count) return;
        viewModel.Select(rows[treeList.SelectedIndex].Node);
        UpdatePaneState();
    }

    private async Task ToggleSelectedTreeNodeAsync()
    {
        var rows = viewModel.Tree.GetVisibleRows();
        if (treeList.SelectedIndex is < 0 || treeList.SelectedIndex >= rows.Count)
        {
            status.Text = "Select a Tree row before expanding it.";
            return;
        }

        var row = rows[treeList.SelectedIndex];
        if (!row.IsExpandable)
        {
            status.Text = "The selected Tree row has no descendants.";
            return;
        }

        if (row.IsExpanded) row.Node.Collapse();
        else await row.Node.ExpandAsync().ConfigureAwait(true);
        RenderRows();
        status.Text = row.IsExpanded ? "Tree row expanded." : "Tree row collapsed.";
    }

    private void SelectExplorerRow()
    {
        if (explorerList.SelectedIndex is < 0 || explorerList.SelectedIndex >= viewModel.ExplorerRows.Count) return;
        viewModel.NavigateToPath(viewModel.ExplorerRows[explorerList.SelectedIndex].Projection.DisplayPath);
        UpdatePaneState();
    }

    private async Task OpenSelectedAsync()
    {
        var result = await viewModel.OpenSelectedInExplorerAsync().ConfigureAwait(true);
        status.Text = result.Succeeded ? "Opened the selected current item." : result.Error ?? "The selected item could not be opened.";
    }

    private void ToggleReplay()
    {
        if (viewModel.Replay.IsPlaying)
        {
            viewModel.Replay.Pause();
            replayPlayButton.Content = "Play replay";
        }
        else
        {
            viewModel.Replay.Play();
            replayPlayButton.Content = "Pause replay";
        }
        UpdatePaneState();
    }

    private void UpdatePaneState()
    {
        var selected = viewModel.SelectedNode?.Projection;
        leftPanePath.Text = $"Left pane: {viewModel.SplitPanes.Left.CurrentPath ?? "no selection"}";
        rightPanePath.Text = $"Right pane: {(viewModel.SplitPanes.IsSplit ? viewModel.SplitPanes.Right.CurrentPath ?? "no selection" : "split disabled")}";
        openButton.IsEnabled = selected?.CanOpenInExplorer == true && !selected.IsDeleted;
    }

    private void UpdateSplitLayout()
    {
        if (resultGrid.Children.Count > 1) resultGrid.Children[1].IsVisible = viewModel.SplitPanes.IsSplit;
    }
}

/// <summary>Maps the Agent's wire diff snapshot to the shared Tree/Explorer projection model.</summary>
internal sealed class AgentDiffProjectionSource : IDiffProjectionSource
{
    private readonly AgentPipeProjectionClient client;

    /// <summary>Initializes the Agent-backed diff source.</summary>
    public AgentDiffProjectionSource(AgentPipeProjectionClient client) => this.client = client ?? throw new ArgumentNullException(nameof(client));

    /// <inheritdoc />
    public async ValueTask<DiffProjection> GetDiffProjectionAsync(DiffProjectionQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var filter = query.Filter.AnyTerms.Count == 0 ? null : query.Filter.AnyTerms[0].Value;
        var response = await client.GetDiffProjectionSnapshotAsync(new DiffProjectionRequest(
            query.FromUtc,
            query.ToUtc,
            query.Mode,
            filter,
            AllFilters: query.Filter.AllTerms.Select(term => term.Value).ToArray(),
            AnyFilters: query.Filter.AnyTerms.Select(term => term.Value).ToArray(),
            ExcludeFilters: query.Filter.ExcludedTerms.Select(term => term.Value).ToArray()), cancellationToken).ConfigureAwait(false);
        var items = (response.RichItems ?? Array.Empty<DiffProjectionItemSnapshot>()).Select(ToProjection).ToArray();
        var unknown = (response.RichUnknownLocationItems ?? Array.Empty<DiffProjectionItemSnapshot>()).Select(ToProjection).ToArray();
        return new DiffProjection(query.Mode, query.ToUtc, items, unknown, response.Items);
    }

    private static FileDiffProjection ToProjection(DiffProjectionItemSnapshot value) => new(
        value.FileId,
        value.DisplayName,
        value.OldPath,
        value.NewPath,
        value.DisplayPath,
        value.Kind,
        value.Quality,
        Enum.Parse<DiffPrimaryOperation>(value.PrimaryOperation),
        Enum.Parse<DiffSemanticState>(value.SemanticState),
        value.SubOperations.Select(Enum.Parse<DiffPrimaryOperation>).ToArray(),
        value.RelatedOperationId,
        value.IsVirtual,
        value.IsDeleted,
        value.IsPeriodOnly,
        value.CanOpenInExplorer,
        value.OpenDisabledReason,
        value.DescendantCount,
        value.RenameHistory,
        value.ReplayTimeline.Select(point => new ReplayTimelinePoint(point.EventId, point.TimeUtc, Enum.Parse<PaneLifecycle>(point.Lifecycle), Enum.Parse<DiffPrimaryOperation>(point.Operation))).ToArray(),
        value.EventIds);
}
