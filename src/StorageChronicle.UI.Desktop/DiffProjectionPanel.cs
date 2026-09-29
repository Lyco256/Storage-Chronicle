using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Input;
using StorageChronicle.Contracts;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Projection;
using StorageChronicle.Settings;
using StorageChronicle.UI.DiffView;
using StorageChronicle.UI.Shared;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace StorageChronicle.UI.Desktop;

/// <summary>Desktop host for the shared Tree/Explorer Diff View and its four time modes.</summary>
public sealed class DiffProjectionPanel : UserControl
{
    private readonly DiffViewModel viewModel;
    private readonly AgentPipeProjectionClient? projectionClient;
    private readonly ObservableCollection<string> treeRows = [];
    private readonly ObservableCollection<string> activityFrameRows = [];
    private readonly ObservableCollection<string> activityTimelineRows = [];
    private readonly ListBox treeList;
    private readonly ListBox activityFrameList;
    private readonly ListBox activityTimelineList;
    private readonly TextBlock activityFrameDetails;
    private readonly Button pinActivityFrameButton;
    private readonly Button moreActivityTimelineButton;
    private readonly Button moreActivityFramesButton;
    private readonly ContentControl explorerHost;
    private readonly TextBlock status;
    private readonly TextBox filter;
    private readonly TextBox address;
    private readonly Grid resultGrid;
    private readonly TextBlock leftPanePath;
    private readonly TextBlock rightPanePath;
    private readonly TextBlock selectionDetails;
    private readonly Button openButton;
    private readonly Button replayPlayButton;
    private readonly Button livePauseButton;
    private readonly Slider zoomSlider;
    private readonly Slider replayTimeline;
    private readonly ComboBox replaySpeed;
    private readonly StackPanel breadcrumbs;
    private readonly DispatcherTimer liveRefreshTimer;
    private readonly DispatcherTimer replayTimer;
    private readonly DispatcherTimer zoomPersistTimer;
    private string? currentPath;
    private bool isUpdatingReplaySlider;
    private bool isLoadingPresentationSettings;
    private Stopwatch replayClock = new();
    private DateTimeOffset replayTimelineStart;
    private DateTimeOffset? currentQueryFromUtc;
    private DateTimeOffset currentQueryToUtc;
    private int activityTimelinePage;
    private int activityTimelineTotal;

    /// <summary>Creates a Diff View backed by the Agent projection pipe.</summary>
    public DiffProjectionPanel(IProjectionService projection)
    {
        projectionClient = projection as AgentPipeProjectionClient;
        viewModel = projection is AgentPipeProjectionClient agent
            ? new DiffViewModel(new AgentDiffProjectionSource(agent))
            : new DiffViewModel(projection);
        liveRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        replayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        zoomPersistTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };

        treeList = new ListBox { ItemsSource = treeRows, [AutomationProperties.NameProperty] = "Diff View tree" };
        activityFrameList = new ListBox { ItemsSource = activityFrameRows, [AutomationProperties.NameProperty] = "Diff Activity Frames", MinHeight = 120 };
        activityTimelineList = new ListBox { ItemsSource = activityTimelineRows, [AutomationProperties.NameProperty] = "Selected Activity Frame event timeline", MaxHeight = 180 };
        activityFrameList.SelectionChanged += async (_, _) => await SelectActivityFrameAsync().ConfigureAwait(true);
        explorerHost = new ContentControl { IsVisible = false, [AutomationProperties.NameProperty] = "Diff View Explorer content" };
        treeList.SelectionChanged += (_, _) => SelectTreeRow();
        status = new TextBlock { Text = "Diff View: Agent connection is idle." };
        filter = new TextBox { PlaceholderText = "Name, path, or operation", [AutomationProperties.NameProperty] = "Diff filter" };
        address = new TextBox { PlaceholderText = "Current virtual path", [AutomationProperties.NameProperty] = "Diff Explorer path" };
        address.KeyDown += async (_, args) =>
        {
            if (args.Key == Key.Enter) await NavigateToAddressAsync().ConfigureAwait(true);
        };
        breadcrumbs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        selectionDetails = new TextBlock { Text = "Select an item to inspect its recorded metadata.", TextWrapping = TextWrapping.Wrap, [AutomationProperties.NameProperty] = "Diff selection details" };
        activityFrameDetails = new TextBlock { Text = "Select an Activity Frame to inspect its process, timing, metrics, and operations.", TextWrapping = TextWrapping.Wrap, [AutomationProperties.NameProperty] = "Activity Frame details" };
        pinActivityFrameButton = new Button { Content = "Pin frame", IsEnabled = false, [AutomationProperties.NameProperty] = "Pin selected Activity Frame" };
        pinActivityFrameButton.Click += (_, _) => ToggleSelectedActivityFramePin();
        var ascendingFrames = new ToggleButton { Content = "Oldest first", [AutomationProperties.NameProperty] = "Sort Activity Frames oldest first" };
        ascendingFrames.IsCheckedChanged += async (_, _) =>
        {
            await viewModel.SetActivityFrameSortAsync(ascendingFrames.IsChecked == true).ConfigureAwait(true);
            RenderActivityFrames();
        };
        moreActivityTimelineButton = new Button { Content = "More events", IsEnabled = false, [AutomationProperties.NameProperty] = "Load more Activity Frame events" };
        moreActivityTimelineButton.Click += async (_, _) => await LoadMoreActivityTimelineAsync().ConfigureAwait(true);
        moreActivityFramesButton = new Button { Content = "More frames", IsEnabled = false, [AutomationProperties.NameProperty] = "Load more Activity Frames" };
        moreActivityFramesButton.Click += async (_, _) => await LoadMoreActivityFramesAsync().ConfigureAwait(true);

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
        back.Click += (_, _) => NavigateHistory(-1);
        var forward = new Button { Content = "Forward", [AutomationProperties.NameProperty] = "Diff navigation forward" };
        forward.Click += (_, _) => NavigateHistory(1);
        openButton = new Button { Content = "Open in Explorer", [AutomationProperties.NameProperty] = "Open selected diff item in Explorer" };
        openButton.Click += async (_, _) => await OpenSelectedAsync().ConfigureAwait(true);
        var navigate = new Button { Content = "Go", [AutomationProperties.NameProperty] = "Navigate to Diff path" };
        navigate.Click += async (_, _) => await NavigateToAddressAsync().ConfigureAwait(true);
        var up = new Button { Content = "Up", [AutomationProperties.NameProperty] = "Navigate to parent Diff folder" };
        up.Click += async (_, _) => await NavigateUpAsync().ConfigureAwait(true);
        var split = new ToggleButton { Content = "Split panes", [AutomationProperties.NameProperty] = "Toggle diff split panes" };
        split.IsCheckedChanged += (_, _) => { viewModel.SplitPanes.SetSplit(split.IsChecked == true); UpdateSplitLayout(); UpdatePaneState(); };
        replayPlayButton = new Button { Content = "Play replay", [AutomationProperties.NameProperty] = "Play or pause diff replay" };
        replayPlayButton.Click += (_, _) => ToggleReplay();
        var present = new Button { Content = "Present", [AutomationProperties.NameProperty] = "Return replay to present" };
        present.Click += (_, _) =>
        {
            viewModel.ReturnReplayToPresent();
            RenderActivityFrames();
            replayTimer.Stop();
            replayPlayButton.Content = "Play replay";
            UpdateReplayControls();
            UpdatePaneState();
        };
        livePauseButton = new Button { Content = "Pause Live", [AutomationProperties.NameProperty] = "Pause or resume Diff Live updates" };
        livePauseButton.Click += async (_, _) => await ToggleLivePauseAsync().ConfigureAwait(true);
        zoomSlider = new Slider { Minimum = 50, Maximum = 300, Value = 100, Width = 110, [AutomationProperties.NameProperty] = "Explorer icon zoom" };
        zoomSlider.ValueChanged += (_, _) =>
        {
            if (zoomSlider.Value is >= DiffExplorerView.MinimumZoomPercent and <= DiffExplorerView.MaximumZoomPercent)
                viewModel.Explorer.SetZoomPercent((int)Math.Round(zoomSlider.Value));
            if (viewModel.ViewMode is ExplorerViewMode.ExtraLargeIcons or ExplorerViewMode.LargeIcons or ExplorerViewMode.MediumIcons or ExplorerViewMode.SmallIcons) RenderRows();
        };
        replayTimeline = new Slider { Minimum = 0, Maximum = 0, Width = 140, [AutomationProperties.NameProperty] = "Replay event timeline" };
        replayTimeline.ValueChanged += (_, _) => SelectReplayPoint();
        replaySpeed = new ComboBox { ItemsSource = new[] { "1×", "2×", "10×" }, SelectedIndex = 0, Width = 72, [AutomationProperties.NameProperty] = "Replay speed" };
        replaySpeed.SelectionChanged += (_, _) => UpdateReplaySpeed();
        var previousEvent = new Button { Content = "◀ Event", [AutomationProperties.NameProperty] = "Replay previous event" };
        previousEvent.Click += (_, _) => StepReplay(-1);
        var nextEvent = new Button { Content = "Event ▶", [AutomationProperties.NameProperty] = "Replay next event" };
        nextEvent.Click += (_, _) => StepReplay(1);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { filter, refresh, presentations, expandTree, back, forward, openButton, address, navigate, up, zoomSlider, split, livePauseButton, replayPlayButton, previousEvent, replayTimeline, nextEvent, replaySpeed, present } };

        leftPanePath = new TextBlock { Text = "Left pane: no selection", [AutomationProperties.NameProperty] = "Diff left pane" };
        rightPanePath = new TextBlock { Text = "Right pane: no selection", [AutomationProperties.NameProperty] = "Diff right pane" };
        var leftPane = new StackPanel { Spacing = 4, Children = { breadcrumbs, leftPanePath, treeList, explorerHost } };
        var rightPane = new StackPanel { Spacing = 4, Children = { rightPanePath, selectionDetails, new TextBlock { Text = "Activity Frames (separate pane per process and location)" }, ascendingFrames, activityFrameList, moreActivityFramesButton, activityFrameDetails, pinActivityFrameButton, activityTimelineList, moreActivityTimelineButton } };
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
        liveRefreshTimer.Tick += async (_, _) =>
        {
            if (viewModel.Mode == DiffMode.Live && !viewModel.IsPaused) await RefreshAsync(DiffMode.Live).ConfigureAwait(true);
        };
        replayTimer.Tick += (_, _) => AdvanceReplay();
        zoomPersistTimer.Tick += async (_, _) =>
        {
            zoomPersistTimer.Stop();
            await PersistZoomAsync().ConfigureAwait(true);
        };
        zoomSlider.ValueChanged += (_, _) =>
        {
            if (isLoadingPresentationSettings) return;
            zoomPersistTimer.Stop();
            zoomPersistTimer.Start();
        };
        AttachedToVisualTree += async (_, _) =>
        {
            await LoadUserPresentationSettingsAsync().ConfigureAwait(true);
            await RefreshAsync(DiffMode.Live).ConfigureAwait(true);
            liveRefreshTimer.Start();
        };
        DetachedFromVisualTree += (_, _) => { liveRefreshTimer.Stop(); replayTimer.Stop(); zoomPersistTimer.Stop(); };
    }

    private async Task RefreshAsync(DiffMode mode)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            var projectionFilter = string.IsNullOrWhiteSpace(filter.Text)
                ? ProjectionFilter.Empty
                : new ProjectionFilter(Any: new[] { new FilterTerm(FilterField.Name, filter.Text.Trim()) });
            currentQueryFromUtc = mode == DiffMode.PointInTime ? null : now.AddHours(-1);
            currentQueryToUtc = now;
            await viewModel.RefreshAsync(new DiffProjectionQuery(currentQueryFromUtc, currentQueryToUtc, mode, projectionFilter)).ConfigureAwait(true);
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

        var rows = viewModel.CurrentPath is null
            ? viewModel.Explorer.GetPage(1, 250)
            : viewModel.Explorer.GetChildrenPage(viewModel.CurrentPath, 1, 250);
        explorerHost.Content = BuildExplorerLayout(rows);
        UpdateAddressControls();
        RenderActivityFrames();
        UpdateReplayControls();
        UpdatePaneState();
    }

    private void ShowTree()
    {
        treeList.IsVisible = true;
        explorerHost.IsVisible = false;
    }

    private void ShowExplorer()
    {
        treeList.IsVisible = false;
        explorerHost.IsVisible = true;
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
            replayTimer.Stop();
            replayPlayButton.Content = "Play replay";
        }
        else
        {
            if (viewModel.GetSelectedReplayTimeline() is not { Count: > 0 })
            {
                status.Text = "Select an item or Activity Frame with recorded replay events first.";
                return;
            }
            if (viewModel.Replay.IsAtPresent) viewModel.SetReplayPoint(0);
            viewModel.Replay.Play();
            ResetReplayClock();
            replayTimer.Start();
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
        selectionDetails.Text = selected is null
            ? "Select an item to inspect its recorded metadata."
            : $"{selected.DisplayName}\n{selected.DisplayPath}\n{selected.Kind} · {selected.PrimaryOperation} · {selected.Quality}\n{(selected.IsDeleted ? "Deleted or virtual" : "Current path")}";
    }

    private void UpdateSplitLayout()
    {
        if (resultGrid.Children.Count > 1) resultGrid.Children[1].IsVisible = viewModel.SplitPanes.IsSplit;
    }

    private void RenderActivityFrames()
    {
        activityFrameRows.Clear();
        foreach (var frame in viewModel.ActivityFrames)
        {
            var duration = frame.LastEventUtc - frame.StartedUtc;
            var operations = string.Join(", ", frame.OperationBreakdown.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}:{pair.Value}"));
            var lifecycle = frame.IsClosedByProcessExit ? "closed on process exit" : frame.IsClosedByCompetingActivity ? "closed by competing activity" : $"until {frame.CloseBoundaryUtc:HH:mm:ss}";
            var pinned = viewModel.IsActivityFramePinned(frame.FrameId) ? "[pinned] " : string.Empty;
            activityFrameRows.Add($"{pinned}{frame.ProcessDisplayName} · {frame.StartedUtc:HH:mm:ss} · {duration.TotalSeconds:0.0}s · {frame.OperationCount} changes / {frame.FileCount} items / Δ{frame.SizeDelta} B · {operations} · {lifecycle}");
        }

        var selectedId = viewModel.SelectedActivityFrame?.FrameId;
        activityFrameList.SelectedIndex = selectedId is null ? -1 : viewModel.ActivityFrames.ToList().FindIndex(frame => frame.FrameId == selectedId);
        UpdateActivityFrameDetails();
        moreActivityFramesButton.IsEnabled = viewModel.HasMoreActivityFrames;
    }

    private async Task LoadMoreActivityFramesAsync()
    {
        try
        {
            if (await viewModel.LoadMoreActivityFramesAsync().ConfigureAwait(true)) RenderActivityFrames();
            moreActivityFramesButton.IsEnabled = viewModel.HasMoreActivityFrames;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or ArgumentException)
        {
            status.Text = $"More Activity Frames unavailable: {exception.Message}";
        }
    }

    private async Task SelectActivityFrameAsync()
    {
        var index = activityFrameList.SelectedIndex;
        var frame = index >= 0 && index < viewModel.ActivityFrames.Count ? viewModel.ActivityFrames[index] : null;
        viewModel.SelectActivityFrame(frame);
        activityTimelinePage = 0;
        activityTimelineTotal = 0;
        activityTimelineRows.Clear();
        UpdateActivityFrameDetails();
        if (frame is not null) await LoadMoreActivityTimelineAsync().ConfigureAwait(true);
        UpdateReplayControls();
    }

    private async Task LoadMoreActivityTimelineAsync()
    {
        if (projectionClient is null || viewModel.SelectedActivityFrame is not { } frame) return;
        try
        {
            var nextPage = activityTimelinePage + 1;
            var response = await projectionClient.GetActivityFrameTimelineAsync(new DiffActivityFrameTimelineRequest(
                frame.FrameId, currentQueryFromUtc, currentQueryToUtc, nextPage, 250)).ConfigureAwait(true);
            activityTimelinePage = response.Page;
            activityTimelineTotal = response.TotalCount;
            foreach (var item in response.Events)
                activityTimelineRows.Add($"{item.TimeUtc:HH:mm:ss.fff} · {item.Operation} · {item.Quality} · {item.DisplayPath}");
            viewModel.SetActivityFrameEvents(viewModel.ActivityFrameEvents.Concat(response.Events));
            moreActivityTimelineButton.IsEnabled = response.HasMore;
            UpdateReplayControls();
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            status.Text = $"Activity Frame timeline unavailable: {exception.Message}";
        }
    }

    private void ToggleSelectedActivityFramePin()
    {
        if (viewModel.SelectedActivityFrame is not { } frame) return;
        var pinned = viewModel.ToggleSelectedActivityFramePin();
        pinActivityFrameButton.Content = pinned ? "Unpin frame" : "Pin frame";
        RenderActivityFrames();
    }

    private void UpdateActivityFrameDetails()
    {
        if (viewModel.SelectedActivityFrame is not { } frame)
        {
            activityFrameDetails.Text = "Select an Activity Frame to inspect its process, timing, metrics, and operations.";
            pinActivityFrameButton.IsEnabled = false;
            pinActivityFrameButton.Content = "Pin frame";
            moreActivityTimelineButton.IsEnabled = false;
            return;
        }

        var duration = frame.LastEventUtc - frame.StartedUtc;
        var operations = string.Join(", ", frame.OperationBreakdown.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}: {pair.Value}"));
        var closeReason = frame.IsClosedByProcessExit ? "Process exit at " : frame.IsClosedByCompetingActivity ? "Competing activity at " : "Timeout boundary ";
        activityFrameDetails.Text = $"{frame.ProcessDisplayName} ({frame.ProcessQuality})\n{frame.DisplayRoute}\nStarted {frame.StartedUtc:O}; last event {frame.LastEventUtc:O}; duration {duration.TotalSeconds:0.0}s\n{frame.OperationCount} changes across {frame.FileCount} items; size Δ {frame.SizeDelta} B\n{operations}\n{closeReason}{frame.CloseBoundaryUtc:O}";
        pinActivityFrameButton.IsEnabled = true;
        pinActivityFrameButton.Content = viewModel.IsActivityFramePinned(frame.FrameId) ? "Unpin frame" : "Pin frame";
        moreActivityTimelineButton.IsEnabled = activityTimelinePage * 250 < activityTimelineTotal;
    }

    private Control BuildExplorerLayout(IReadOnlyList<DiffExplorerRow> rows)
    {
        var mode = viewModel.ViewMode;
        Panel panel = mode is ExplorerViewMode.ExtraLargeIcons or ExplorerViewMode.LargeIcons or ExplorerViewMode.MediumIcons or ExplorerViewMode.SmallIcons or ExplorerViewMode.Tiles
            ? new WrapPanel { Orientation = Orientation.Horizontal }
            : new StackPanel { Spacing = 2 };
        foreach (var row in rows)
        {
            var projection = row.Projection;
            var iconSize = mode switch
            {
                ExplorerViewMode.ExtraLargeIcons => zoomSlider.Value * 0.62,
                ExplorerViewMode.LargeIcons => zoomSlider.Value * 0.44,
                ExplorerViewMode.MediumIcons => zoomSlider.Value * 0.28,
                ExplorerViewMode.SmallIcons => zoomSlider.Value * 0.18,
                _ => 18
            };
            var icon = new TextBlock
            {
                Text = row.Visuals.Primary.IconKey,
                FontSize = Math.Clamp(iconSize, 12, 192),
                Foreground = BrushFor(projection.SemanticState),
                VerticalAlignment = VerticalAlignment.Center,
                [AutomationProperties.NameProperty] = $"{projection.PrimaryOperation} icon"
            };
            var details = new TextBlock
            {
                Text = mode switch
                {
                    ExplorerViewMode.Details => $"{projection.DisplayName}    {projection.Kind}    {projection.PrimaryOperation}    {projection.Quality}    {projection.DisplayPath}",
                    ExplorerViewMode.List => $"{projection.DisplayName}    {projection.PrimaryOperation}    {projection.Quality}",
                    ExplorerViewMode.Tiles => $"{projection.DisplayName}\n{projection.Kind} · {projection.PrimaryOperation}",
                    ExplorerViewMode.Content => $"{projection.DisplayName}\n{projection.DisplayPath}\n{projection.PrimaryOperation} · {projection.Quality}",
                    _ => projection.DisplayName
                },
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            var markers = new TextBlock
            {
                Text = string.Join(" · ", row.Visuals.Secondary.Select(marker => marker.IconKey).Append(projection.Quality.ToString())),
                TextWrapping = TextWrapping.Wrap,
                FontSize = Math.Max(10, 12 * zoomSlider.Value / 100),
                VerticalAlignment = VerticalAlignment.Center,
                [AutomationProperties.NameProperty] = $"Secondary operations and quality: {string.Join(", ", row.Visuals.Secondary.Select(marker => marker.Kind).Append(projection.Quality.ToString()))}"
            };
            var body = mode is ExplorerViewMode.ExtraLargeIcons or ExplorerViewMode.LargeIcons or ExplorerViewMode.MediumIcons or ExplorerViewMode.SmallIcons
                ? new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Children = { icon, details, markers } }
                : new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { icon, details, markers } };
            var openState = row.CanOpenInExplorer ? string.Empty : $" — {row.OpenDisabledReason}";
            var button = new Button
            {
                Content = body,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                [AutomationProperties.NameProperty] = $"{projection.DisplayName}, {projection.Kind}, {projection.PrimaryOperation}, {projection.Quality}{openState}",
                Margin = new Avalonia.Thickness(2),
                Padding = new Avalonia.Thickness(8),
                Background = BrushFor(projection.SemanticState, 0.14)
            };
            ToolTip.SetTip(button, $"{projection.DisplayPath}{Environment.NewLine}{projection.Quality}{openState}");
            if (mode is ExplorerViewMode.ExtraLargeIcons or ExplorerViewMode.LargeIcons or ExplorerViewMode.MediumIcons or ExplorerViewMode.SmallIcons or ExplorerViewMode.Tiles)
            {
                button.Width = Math.Max(100, zoomSlider.Value * 1.2);
                button.Height = Math.Max(78, zoomSlider.Value * 0.9);
            }
            button.Click += (_, _) =>
            {
                if (projection.Kind == FileKind.Directory)
                {
                    viewModel.NavigateToLocation(projection.DisplayPath);
                }
                else
                {
                    viewModel.NavigateToPath(projection.DisplayPath);
                    currentPath = ParentPath(projection.DisplayPath);
                    if (currentPath is not null) viewModel.NavigateToLocation(currentPath, preserveSelection: true);
                }
                currentPath = viewModel.CurrentPath;
                address.Text = currentPath ?? string.Empty;
                UpdateAddressControls();
                RenderRows();
                UpdatePaneState();
            };
            panel.Children.Add(button);
        }
        return new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private async Task LoadUserPresentationSettingsAsync()
    {
        if (viewModel is null) return;
        try
        {
            if (projectionClient is not null)
            {
                isLoadingPresentationSettings = true;
                var settings = await projectionClient.LoadUserSettingsAsync().ConfigureAwait(true);
                viewModel.Explorer.SetZoomPercent(settings.DiffZoomPercent);
                zoomSlider.Value = settings.DiffZoomPercent;
                viewModel.SetViewMode(settings.DiffFormat switch
                {
                    DiffDisplayFormat.Unified => ExplorerViewMode.Details,
                    DiffDisplayFormat.SideBySide => ExplorerViewMode.List,
                    _ => ExplorerViewMode.Details
                });
            }
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or InvalidDataException)
        {
            status.Text = $"Diff View preferences could not be loaded: {exception.Message}";
        }
        finally
        {
            isLoadingPresentationSettings = false;
        }
    }

    private async Task ToggleLivePauseAsync()
    {
        if (viewModel.IsPaused)
        {
            await viewModel.ResumeLiveAsync().ConfigureAwait(true);
            livePauseButton.Content = "Pause Live";
            await RefreshAsync(DiffMode.Live).ConfigureAwait(true);
        }
        else
        {
            viewModel.PauseLive();
            livePauseButton.Content = "Resume Live";
        }
    }

    private void UpdateReplaySpeed()
    {
        var speed = replaySpeed.SelectedIndex switch { 1 => 2d, 2 => 10d, _ => 1d };
        viewModel.Replay.SetSpeed(speed);
        if (viewModel.Replay.IsPlaying) ResetReplayClock();
    }

    private void StepReplay(int direction)
    {
        replayTimer.Stop();
        viewModel.Replay.Pause();
        replayPlayButton.Content = "Play replay";
        if (!viewModel.StepReplay(direction)) return;
        RenderActivityFrames();
        UpdateReplayControls();
        UpdatePaneState();
    }

    private void AdvanceReplay()
    {
        var timeline = viewModel.GetSelectedReplayTimeline();
        if (timeline is not { Count: > 0 })
        {
            viewModel.Replay.Pause();
            replayTimer.Stop();
            replayPlayButton.Content = "Play replay";
            return;
        }

        var replayTarget = replayTimelineStart + TimeSpan.FromTicks((long)(replayClock.Elapsed.Ticks * viewModel.Replay.Speed));
        var next = GetReplayIndex(timeline);
        while (next + 1 < timeline.Count && timeline[next + 1].TimeUtc <= replayTarget) next++;
        if (next + 1 >= timeline.Count && replayTarget >= timeline[^1].TimeUtc)
        {
            next = timeline.Count - 1;
            viewModel.Replay.Pause();
            replayTimer.Stop();
            replayPlayButton.Content = "Play replay";
        }
        viewModel.SetReplayPoint(next);
        RenderActivityFrames();
        UpdateReplayControls();
        UpdatePaneState();
    }

    private int GetReplayIndex(IReadOnlyList<ReplayTimelinePoint> timeline)
    {
        if (viewModel.Replay.CursorUtc is not { } cursor) return 0;
        var index = -1;
        for (var candidate = 0; candidate < timeline.Count; candidate++)
        {
            if (timeline[candidate].TimeUtc == cursor) index = candidate;
        }
        return index < 0 ? 0 : index;
    }

    private async Task PersistZoomAsync()
    {
        if (projectionClient is null) return;
        try
        {
            viewModel.Explorer.SetZoomPercent((int)Math.Round(zoomSlider.Value));
            var settings = await projectionClient.LoadUserSettingsAsync().ConfigureAwait(true);
            var result = await projectionClient.ApplyUserSettingsAsync(settings with { DiffZoomPercent = (int)Math.Round(zoomSlider.Value) }).ConfigureAwait(true);
            if (!result.Succeeded) status.Text = $"Diff View zoom was not saved: {result.Error}";
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or InvalidDataException)
        {
            status.Text = $"Diff View zoom could not be saved: {exception.Message}";
        }
    }

    private void SelectReplayPoint()
    {
        if (isUpdatingReplaySlider || viewModel.GetSelectedReplayTimeline() is not { Count: > 0 }) return;
        viewModel.SetReplayPoint((int)replayTimeline.Value);
        RenderActivityFrames();
        if (viewModel.Replay.IsPlaying) ResetReplayClock();
        UpdatePaneState();
    }

    private void UpdateReplayControls()
    {
        var timeline = viewModel.GetSelectedReplayTimeline();
        var count = timeline?.Count ?? 0;
        isUpdatingReplaySlider = true;
        replayTimeline.Maximum = Math.Max(0, count - 1);
        var index = timeline is null ? 0 : viewModel.Replay.IsAtPresent ? Math.Max(0, count - 1) : GetReplayIndex(timeline);
        replayTimeline.Value = Math.Clamp(index < 0 ? 0 : index, 0, Math.Max(0, count - 1));
        replayTimeline.IsEnabled = count > 0;
        isUpdatingReplaySlider = false;
    }

    private async Task NavigateToAddressAsync()
    {
        var path = address.Text?.Trim();
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!viewModel.NavigateToLocation(path))
        {
            status.Text = "Enter a rooted path or a projected virtual location.";
            return;
        }
        currentPath = viewModel.CurrentPath;
        UpdateAddressControls();
        RenderRows();
        await Task.CompletedTask;
    }

    private async Task NavigateUpAsync()
    {
        if (!viewModel.NavigateUp()) return;
        currentPath = viewModel.CurrentPath;
        address.Text = currentPath;
        UpdateAddressControls();
        RenderRows();
        await Task.CompletedTask;
    }

    private void UpdateAddressControls()
    {
        breadcrumbs.Children.Clear();
        var path = viewModel.CurrentPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            breadcrumbs.Children.Add(new TextBlock { Text = "All changes", VerticalAlignment = VerticalAlignment.Center });
            return;
        }
        var crumbs = viewModel.Breadcrumbs;
        foreach (var breadcrumb in crumbs)
        {
            var crumb = new Button { Content = breadcrumb.Label, Padding = new Avalonia.Thickness(4, 2), [AutomationProperties.NameProperty] = $"Navigate to {breadcrumb.Path}" };
            crumb.Click += async (_, _) =>
            {
                if (!viewModel.NavigateToBreadcrumb(breadcrumb.Index)) return;
                currentPath = viewModel.CurrentPath;
                address.Text = currentPath;
                UpdateAddressControls();
                RenderRows();
                await Task.CompletedTask;
            };
            breadcrumbs.Children.Add(crumb);
            if (breadcrumb.Index < crumbs.Count - 1) breadcrumbs.Children.Add(new TextBlock { Text = "›", VerticalAlignment = VerticalAlignment.Center });
        }
    }

    private void NavigateHistory(int direction)
    {
        var navigated = direction < 0 ? viewModel.NavigateBack() : viewModel.NavigateForward();
        if (!navigated) return;
        currentPath = viewModel.CurrentPath;
        address.Text = currentPath;
        UpdateAddressControls();
        RenderRows();
    }

    private void ResetReplayClock()
    {
        replayTimelineStart = viewModel.Replay.CursorUtc ?? DateTimeOffset.UtcNow;
        replayClock.Restart();
    }

    private static string? ParentPath(string path)
    {
        return DiffPathNavigation.GetParent(path);
    }

    private static IBrush BrushFor(DiffSemanticState state, double opacity = 1) => new SolidColorBrush(state switch
    {
        DiffSemanticState.Added => Color.Parse("#2EAD63"),
        DiffSemanticState.Removed => Color.Parse("#E05252"),
        DiffSemanticState.Edited => Color.Parse("#4098E5"),
        DiffSemanticState.Moved or DiffSemanticState.Renamed => Color.Parse("#B272E8"),
        DiffSemanticState.Shared => Color.Parse("#45B7A2"),
        DiffSemanticState.Cloud => Color.Parse("#55A7C8"),
        _ => Color.Parse("#AAB3BD")
    }, opacity);
}

/// <summary>Maps the Agent's wire diff snapshot to the shared Tree/Explorer projection model.</summary>
internal sealed class AgentDiffProjectionSource : IDiffProjectionSource
{
    private readonly AgentPipeProjectionClient client;

    /// <summary>Initializes the Agent-backed diff source.</summary>
    public AgentDiffProjectionSource(AgentPipeProjectionClient client) => this.client = client ?? throw new ArgumentNullException(nameof(client));

    /// <inheritdoc />
    public async ValueTask<DiffProjectionBundle> GetDiffProjectionAsync(DiffProjectionQuery query, CancellationToken cancellationToken = default)
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
            ExcludeFilters: query.Filter.ExcludedTerms.Select(term => term.Value).ToArray(),
            ActivityFramesPage: query.ActivityFramesPage,
            ActivityFramesPageSize: query.ActivityFramesPageSize,
            ActivityFramesAscending: query.ActivityFramesAscending), cancellationToken).ConfigureAwait(false);
        var items = (response.RichItems ?? Array.Empty<DiffProjectionItemSnapshot>()).Select(ToProjection).ToArray();
        var unknown = (response.RichUnknownLocationItems ?? Array.Empty<DiffProjectionItemSnapshot>()).Select(ToProjection).ToArray();
        return new DiffProjectionBundle(new DiffProjection(query.Mode, query.ToUtc, items, unknown, response.Items), response.ActivityFrames ?? Array.Empty<DiffActivityFrameSnapshot>(), response.ActivityFramesPage, response.ActivityFramesPageSize, response.ActivityFramesTotalCount, response.HasMoreActivityFrames);
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
