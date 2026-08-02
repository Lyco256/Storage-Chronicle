using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Automation;
using System.Collections.ObjectModel;
using StorageChronicle.Contracts;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Projection;
using StorageChronicle.UI.DiffView;
using StorageChronicle.UI.Shared;

namespace StorageChronicle.UI.Desktop;

/// <summary>Small desktop host for the shared Diff View model and all four time modes.</summary>
public sealed class DiffProjectionPanel : UserControl
{
    private readonly DiffViewModel viewModel;
    private readonly ObservableCollection<string> treeRows = [];
    private readonly ObservableCollection<string> explorerRows = [];
    private readonly ListBox treeList;
    private readonly ListBox explorerList;
    private readonly TextBlock status;
    private readonly TextBox filter;

    /// <summary>Creates a Diff View backed by the Agent projection pipe.</summary>
    public DiffProjectionPanel(IProjectionService projection)
    {
        viewModel = projection is AgentPipeProjectionClient agent
            ? new DiffViewModel(new AgentDiffProjectionSource(agent))
            : new DiffViewModel(projection);
        treeList = new ListBox { ItemsSource = treeRows, [AutomationProperties.NameProperty] = "Diff View tree" };
        explorerList = new ListBox { ItemsSource = explorerRows, IsVisible = false, [AutomationProperties.NameProperty] = "Diff View Explorer" };
        status = new TextBlock { Text = "Diff View: Agent connection is idle." };
        filter = new TextBox { PlaceholderText = "Name, path, or operation", [AutomationProperties.NameProperty] = "Diff filter" };
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
        treeButton.Click += (_, _) => { treeList.IsVisible = true; explorerList.IsVisible = false; };
        var explorerButton = new Button { Content = "Explorer", [AutomationProperties.NameProperty] = "Show diff Explorer" };
        explorerButton.Click += (_, _) => { treeList.IsVisible = false; explorerList.IsVisible = true; };
        var presentations = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { treeButton, explorerButton } };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { filter, refresh, presentations } };

        Content = new DockPanel { Margin = new Avalonia.Thickness(16), Children =
        {
            new StackPanel { Orientation = Orientation.Vertical, Spacing = 8, Children = { modes, toolbar, status, treeList, explorerList } }
        }};
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
            treeRows.Clear();
            foreach (var row in viewModel.Tree.GetVisibleRows())
            {
                treeRows.Add($"{new string(' ', row.Depth * 2)}{row.Visuals.Primary.IconKey} {row.Node.Projection.DisplayName} ({row.Node.Projection.DescendantCount})");
            }
            explorerRows.Clear();
            foreach (var row in viewModel.ExplorerRows)
            {
                var openState = row.CanOpenInExplorer ? "" : $" [{row.OpenDisabledReason}]";
                explorerRows.Add($"{row.Visuals.Primary.IconKey} {row.Projection.DisplayName} — {row.Projection.DisplayPath}{openState}");
            }
            status.Text = $"{mode}: {viewModel.Items.Count} rows";
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            status.Text = $"Agent unavailable: {exception.Message}";
        }
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
