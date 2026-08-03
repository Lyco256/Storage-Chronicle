using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.UI.EventStack;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(StorageChronicle.UI.EventStack.Tests.EventStackTestApp))]

namespace StorageChronicle.UI.EventStack.Tests;

public sealed class EventStackViewModelTests
{
    [Fact]
    public async Task ThreeModesKeepSelectionAndUseOneQueryState()
    {
        var source = new FakeProjection();
        var view = new EventStackViewModel(source);
        await view.LoadPageAsync();
        await view.SelectAsync(view.Rows[0]);
        var selected = view.SelectedItem!.EventId;

        await view.SetModeAsync(EventStackMode.Source);
        await view.SetModeAsync(EventStackMode.Normalized);
        await view.SetModeAsync(EventStackMode.Grouped);

        Assert.Equal(EventStackMode.Grouped, view.Mode);
        Assert.Equal(selected, view.SelectedItem!.EventId);
        Assert.Equal(EventStackMode.Grouped, source.Queries[^1].Mode);
        Assert.Equal(EventStackSortDirection.Descending, source.Queries[^1].SortDirection);
    }

    [Fact]
    public async Task ExpansionDoesNotCreateRowsOutsideTheBoundedPage()
    {
        var source = new FakeProjection { HasChildren = true };
        var view = new EventStackViewModel(source);
        await view.LoadPageAsync(1, 25);
        Assert.Single(view.Rows);
        Assert.True(view.Rows[0].HasChildren);

        view.ToggleExpansion(view.Rows[0]);

        Assert.True(view.Rows[0].IsExpanded);
        Assert.Single(view.Rows[0].Children);
        Assert.Equal(25, source.Queries[^1].PageSize);
    }

    [Fact]
    public async Task GroupFileNormalizedAndSourceChildrenExpandIndependently()
    {
        var source = new FakeProjection { HasChildren = true, HasNestedChildren = true };
        var view = new EventStackViewModel(source);
        await view.LoadPageAsync(1, 25);

        var group = view.Rows[0];
        Assert.True(group.HasChildren);
        view.ToggleExpansion(group);
        var file = group.Children[0];
        Assert.True(file.HasChildren);
        Assert.False(file.IsExpanded);

        file.ToggleExpansionCommand.Execute(null);
        var normalized = file.Children[0];
        Assert.True(file.IsExpanded);
        Assert.True(normalized.HasChildren);
        Assert.False(normalized.IsExpanded);

        normalized.ToggleExpansionCommand.Execute(null);
        Assert.True(normalized.IsExpanded);
        Assert.Single(normalized.Children);
    }

    [Fact]
    public async Task PagingOrderingAndPageBoundaryAreObservable()
    {
        var source = new FakeProjection { PageCount = 3 };
        var view = new EventStackViewModel(source);
        await view.LoadPageAsync(1, 10);
        await view.SetSortDirectionAsync(EventStackSortDirection.Ascending);
        await view.NextPageAsync();

        Assert.Equal(2, view.CurrentPage);
        Assert.True(view.CanGoPrevious);
        Assert.True(view.HasMore);
        Assert.Equal(EventStackSortDirection.Ascending, source.Queries[^1].SortDirection);
        Assert.Equal(2, source.Queries[^1].Page);
    }

    [Fact]
    public async Task LiveFollowStopsOnHistoryScrollAndResumesAtCurrent()
    {
        var source = new FakeProjection();
        var view = new EventStackViewModel(source);
        await view.LoadPageAsync();
        var queryCount = source.Queries.Count;
        view.OnScrolledAwayFromTail();
        await view.OnLiveUpdateAsync();
        Assert.False(view.IsFollowing);
        Assert.Equal(queryCount, source.Queries.Count);

        await view.ReturnToCurrentAsync();
        Assert.True(view.IsFollowing);
        Assert.Equal(1, source.Queries[^1].Page);
    }

    [Fact]
    public async Task FiltersSavedFiltersQualityAndUnknownProcessAreVisible()
    {
        var source = new FakeProjection { Quality = EventQuality.Correlated, ProcessName = null };
        var view = new EventStackViewModel(source);
        await view.ApplyFilterAsync("report", EventQuality.Correlated, EventOrigin.Etw, CanonicalOperation.DataWrite);
        view.SaveCurrentFilter("Correlated reports");

        Assert.Equal("report", source.Queries[^1].Filter.SearchText);
        Assert.Equal(EventQuality.Correlated, source.Queries[^1].Filter.Quality);
        Assert.Single(view.SavedFilters);
        Assert.Equal("Correlated", view.Rows[0].QualityDisplay);
        Assert.Equal("Unknown process", view.Rows[0].ProcessDisplay);
    }

    [Fact]
    public async Task LargeProjectionMaterializesOnlyRequestedPage()
    {
        var source = new FakeProjection { TotalCount = 100_000, PageCount = 10_000, PageItemCount = 50 };
        var view = new EventStackViewModel(source);
        await view.LoadPageAsync(1, 50);

        Assert.Equal(50, view.Rows.Count);
        Assert.Equal(100_000, view.TotalCount);
        Assert.Equal(50, source.LastMaterializedCount);
    }

    [AvaloniaFact]
    public async Task HeadlessAvaloniaViewUsesCompiledBindingsAndVirtualizedRows()
    {
        var source = new FakeProjection();
        var viewModel = new EventStackViewModel(source);
        await viewModel.LoadPageAsync();
        var view = new EventStackView(viewModel);

        view.Measure(new Size(1024, 768));
        view.Arrange(new Rect(0, 0, 1024, 768));

        var rows = view.FindControl<ListBox>("RowsList");
        Assert.NotNull(rows);
        Assert.Same(viewModel.Rows, rows!.ItemsSource);
        Assert.Equal("Event Stack", view.GetValue(Avalonia.Automation.AutomationProperties.NameProperty));
    }

    [Fact]
    public async Task KeyboardNavigationAndDetailsExposeProcessMovement()
    {
        var source = new FakeProjection();
        var view = new EventStackViewModel(source);
        await view.LoadPageAsync();
        await view.HandleKeyAsync(EventStackKey.Enter);
        Assert.True(view.IsDetailsOpen);
        Assert.NotNull(view.SelectedDetails);

        view.NavigateToParentProcess();
        Assert.NotNull(view.RequestedProcess);
        view.NavigateToChildProcess(source.ChildProcess);
        Assert.Equal(source.ChildProcess, view.RequestedProcess);
    }

    private sealed class FakeProjection : IEventStackProjection
    {
        public List<EventStackQuery> Queries { get; } = [];
        public bool HasChildren { get; init; }
        public bool HasNestedChildren { get; init; }
        public int PageCount { get; init; } = 1;
        public int PageItemCount { get; init; } = 1;
        public int TotalCount { get; init; } = 1;
        public int LastMaterializedCount { get; private set; }
        public EventQuality Quality { get; init; } = EventQuality.Exact;
        public string? ProcessName { get; init; } = "explorer.exe";
        public ProcessInstanceId ChildProcess { get; } = ProcessInstanceId.Create("child-process");

        public ValueTask<EventStackPage> GetPageAsync(EventStackQuery query, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            var rows = Enumerable.Range(0, PageItemCount).Select(index => CreateRow(index, query.Page)).ToArray();
            LastMaterializedCount = rows.Length;
            return ValueTask.FromResult(new EventStackPage(rows.Select(row => CreateItem(row, query)).ToArray(), query.Page, query.PageSize, TotalCount, query.Page < PageCount));
        }

        private EventStackItem CreateItem(EventStackRow row, EventStackQuery query)
        {
            if (!HasChildren) return new EventStackItem(row, Array.Empty<EventStackRow>(), row.Summary, ProcessName, query.Mode == EventStackMode.Grouped, query.ExpandedGroups.Contains(row.Id));
            if (!HasNestedChildren) return new EventStackItem(row, [CreateRow(999, query.Page)], row.Summary, ProcessName, query.Mode == EventStackMode.Grouped, query.ExpandedGroups.Contains(row.Id));

            var source = new EventStackItem(CreateRow(1001, query.Page), Array.Empty<EventStackRow>(), "source", ProcessName, false, false);
            var normalized = new EventStackItem(CreateRow(1000, query.Page), [source.Row], "normalized", ProcessName, false, false, [source]);
            var file = new EventStackItem(CreateRow(999, query.Page), [normalized.Row], "file", ProcessName, false, false, [normalized]);
            return new EventStackItem(row, [file.Row], row.Summary, ProcessName, query.Mode == EventStackMode.Grouped, query.ExpandedGroups.Contains(row.Id), [file]);
        }

        public ValueTask<EventStackDetails?> GetDetailsAsync(EventId eventId, CancellationToken cancellationToken = default)
        {
            var row = CreateRow(0, 1);
            return ValueTask.FromResult<EventStackDetails?>(new EventStackDetails(row, EventOrigin.Etw, Quality, row.TimeUtc, TimeSpan.FromHours(9), new SourceSequence(12), new MountSequence(4), ProcessName, ProcessInstanceId.Create("parent-process"), [ChildProcess], false, false, false));
        }

        private EventStackRow CreateRow(int index, int page) => new(new EventId(Guid.Parse($"00000000-0000-0000-0000-{index + 1:D12}")), DateTimeOffset.UtcNow.AddMinutes(-(page * 10 + index)), "root", CanonicalOperation.DataWrite, $"row-{index}", Quality, null, ProcessAttributionQuality.Unknown, EventOrigin.LiveUsn, ImmutableArray<EventId>.Empty);
    }
}

/// <summary>Minimal Avalonia application used by Headless real-control tests.</summary>
public sealed class EventStackTestApp : Application
{
    /// <summary>Creates the Avalonia headless test platform.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<EventStackTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
