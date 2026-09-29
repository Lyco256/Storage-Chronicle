using StorageChronicle.Domain.Contracts;
using StorageChronicle.Projection;
using StorageChronicle.UI.DiffView;
using Xunit;

namespace StorageChronicle.UI.DiffView.Tests;

public sealed class DiffViewModelTests
{
    private static readonly string[] BreadcrumbLabels = ["/", "virtual", "deleted", "archive"];
    private static readonly string[] DriveBreadcrumbLabels = ["C:\\", "folder", "child"];
    private static readonly double[] ReplaySpeeds = [1d, 2d, 10d];
    private static readonly string[] RootFolderChildren = ["current.txt", "deleted.txt", "nested"];
    [Fact]
    public async Task TreeAndExplorerUseOneProjectionAndExposeAllEightModes()
    {
        var source = new FakeProjectionSource();
        var view = new DiffViewModel(source);
        await view.RefreshAsync(new DiffProjectionQuery(null, Fixture.ToUtc, DiffMode.Period, ProjectionFilter.Empty), TestContext.Current.CancellationToken);

        Assert.Equal(5, view.Items.Count);
        Assert.Equal(5, view.ExplorerRows.Count);
        Assert.Equal(5, view.Rows.Count);
        foreach (var mode in Enum.GetValues<ExplorerViewMode>())
        {
            view.SetViewMode(mode);
            Assert.Equal(mode, view.ViewMode);
            Assert.NotEmpty(view.Explorer.GetPage(1, 50));
        }
    }

    [Fact]
    public async Task ExplorerZoomIsContinuousAndBoundedAcrossPresentationModes()
    {
        var view = await Fixture.CreateViewAsync();
        Assert.Equal(100, view.Explorer.ZoomPercent);
        view.Explorer.SetZoomPercent(173);
        Assert.Equal(173, view.Explorer.ZoomPercent);
        view.Explorer.SetZoomPercent(DiffExplorerView.MinimumZoomPercent);
        view.Explorer.AdjustZoom(-1);
        Assert.Equal(50, view.Explorer.ZoomPercent);
        view.Explorer.SetZoomPercent(DiffExplorerView.MaximumZoomPercent);
        view.Explorer.AdjustZoom(1);
        Assert.Equal(300, view.Explorer.ZoomPercent);
        Assert.Throws<ArgumentOutOfRangeException>(() => view.Explorer.SetZoomPercent(49));
        Assert.Throws<ArgumentOutOfRangeException>(() => view.Explorer.SetZoomPercent(301));
        view.SetViewMode(ExplorerViewMode.List);
        Assert.Equal(300, view.Explorer.ZoomPercent);
    }

    [Fact]
    public async Task TreeUsesGuttersVirtualRootsAndLazyDescendantMaterialization()
    {
        var view = await Fixture.CreateViewAsync();

        var filesystemRoot = Assert.Single(view.RootNodes, node => node.NodeId == "/");
        Assert.True(filesystemRoot.HasUnloadedChildren);
        Assert.Empty(filesystemRoot.Children);
        await filesystemRoot.ExpandAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(filesystemRoot.Children);
        await filesystemRoot.Children.Single(node => node.Projection.DisplayName == "root").ExpandAsync(TestContext.Current.CancellationToken);
        Assert.Contains(view.Tree.GetVisibleRows(), row => row.Visuals.Primary.IconKey == "edit");
        var unknown = Assert.Single(view.RootNodes, node => node.NodeId == "unknown-location");
        Assert.True(unknown.IsVirtual);
        Assert.True(unknown.HasUnloadedChildren);
        Assert.Contains(view.Items, row => row.IsVirtual && row.IsDeleted);
    }

    [Fact]
    public async Task ExplorerRejectsVirtualDeletedRowsAndOpensExistingRowsThroughAdapter()
    {
        var launcher = new RecordingLauncher();
        var view = await Fixture.CreateViewAsync(launcher);
        var deleted = view.ExplorerRows.Single(row => row.Projection.IsDeleted);
        Assert.True(view.NavigateToPath(deleted.Projection.DisplayPath));

        var rejected = await view.OpenSelectedInExplorerAsync(TestContext.Current.CancellationToken);
        Assert.False(rejected.Succeeded);
        Assert.Empty(launcher.Paths);

        var current = view.ExplorerRows.Single(row => row.Projection.DisplayPath == "/root/current.txt");
        Assert.True(view.NavigateToPath(current.Projection.DisplayPath));
        var opened = await view.OpenSelectedInExplorerAsync(TestContext.Current.CancellationToken);
        Assert.True(opened.Succeeded);
        Assert.Equal("/root/current.txt", Assert.Single(launcher.Paths));
    }

    [Fact]
    public async Task NavigationAndSplitPanesSynchronizeSelection()
    {
        var view = await Fixture.CreateViewAsync();
        view.SplitPanes.SetSplit(true);
        view.SplitPanes.SetRatio(0.6);
        Assert.True(view.SplitPanes.IsSplit);
        Assert.Equal(0.6, view.SplitPanes.SplitRatio);

        Assert.True(view.NavigateToPath("/root/current.txt"));
        Assert.True(view.NavigateToPath("/new/destination.txt"));
        Assert.True(view.CanNavigateBack);
        Assert.True(view.NavigateBack());
        Assert.Equal("/root/current.txt", view.SplitPanes.Left.CurrentPath);
        Assert.True(view.NavigateForward());
        Assert.Equal("/new/destination.txt", view.SplitPanes.Right.CurrentPath);
    }

    [Fact]
    public async Task DirectPathBreadcrumbUpAndHistoryWorkWithoutProjectionLookup()
    {
        var source = new FakeProjectionSource();
        var view = new DiffViewModel(source);
        await view.RefreshAsync(null, Fixture.ToUtc, DiffMode.Period, TestContext.Current.CancellationToken);

        Assert.False(view.NavigateToLocation("relative/path"));
        Assert.False(view.NavigateToLocation("/root/../secret"));
        Assert.False(DiffPathNavigation.TryNormalizeDirectPath("/bad\0path", out _));
        Assert.Equal(DriveBreadcrumbLabels, DiffPathNavigation.BuildBreadcrumbs("C:\\folder\\child").Select(crumb => crumb.Label));
        Assert.Equal("C:\\", DiffPathNavigation.GetParent("C:\\folder"));
        Assert.Equal("\\\\server\\share\\", DiffPathNavigation.GetParent("\\\\server\\share\\folder"));
        Assert.Equal("\\\\server\\share\\", Assert.Single(DiffPathNavigation.BuildBreadcrumbs("\\\\server\\share")).Path);
        Assert.True(view.NavigateToLocation("/virtual/deleted/archive"));
        Assert.Equal(BreadcrumbLabels, view.Breadcrumbs.Select(crumb => crumb.Label));
        Assert.True(view.NavigateToBreadcrumb(1));
        Assert.Equal("/virtual", view.CurrentPath);
        Assert.True(view.NavigateToLocation("/virtual/deleted/archive"));
        Assert.True(view.NavigateUp());
        Assert.Equal("/virtual/deleted", view.CurrentPath);
        Assert.True(view.NavigateBack());
        Assert.Equal("/virtual/deleted/archive", view.CurrentPath);
        Assert.True(view.NavigateForward());
        Assert.Equal("/virtual/deleted", view.CurrentPath);
        Assert.Equal(1, source.Calls);

        var deleted = view.ExplorerRows.Single(row => row.Projection.IsDeleted);
        Assert.True(view.NavigateToLocation(deleted.Projection.DisplayPath));
        Assert.Equal(deleted.Projection.DisplayPath, view.CurrentPath);
        Assert.NotEmpty(view.Breadcrumbs);
    }

    [Fact]
    public async Task NavigationHistoryDropsForwardEntriesAfterNewLocation()
    {
        var view = await Fixture.CreateViewAsync();
        Assert.True(view.NavigateToLocation("/one"));
        Assert.True(view.NavigateToLocation("/two"));
        Assert.True(view.NavigateBack());
        Assert.True(view.NavigateToLocation("/three"));
        Assert.False(view.CanNavigateForward);
        Assert.Equal("/three", view.CurrentPath);
    }

    [Fact]
    public async Task MoveEndpointsNavigateToOneAnother()
    {
        var view = await Fixture.CreateViewAsync();
        var source = view.ExplorerRows.Single(row => row.Projection.DisplayPath == "/old/source.txt");
        Assert.True(view.NavigateToPath(source.Projection.DisplayPath));
        var sourceNode = view.SelectedNode!;

        var partner = view.GetMovePartner(sourceNode);
        Assert.NotNull(partner);
        Assert.Equal("/new/destination.txt", partner.Projection.DisplayPath);
        Assert.True(view.NavigateToMovePartner(sourceNode));
        Assert.Equal("/new/destination.txt", view.SelectedNode?.Projection.DisplayPath);
    }

    [Fact]
    public async Task LivePauseLeavesProjectionUntouchedAndReplaySupportsTimelineAndReturn()
    {
        var source = new FakeProjectionSource();
        var view = new DiffViewModel(source);
        await view.RefreshAsync(null, Fixture.ToUtc, DiffMode.Replay, TestContext.Current.CancellationToken);
        var replayRow = view.ExplorerRows.Single(row => row.Projection.ReplayTimeline.Count == 2);
        Assert.True(view.NavigateToPath(replayRow.Projection.DisplayPath));
        Assert.True(view.SetReplayPoint(1));
        Assert.Equal(Fixture.ToUtc, view.Replay.CursorUtc);
        Assert.Equal(ReplaySpeeds, DiffReplayState.SupportedSpeeds);
        Assert.Throws<ArgumentOutOfRangeException>(() => view.Replay.SetSpeed(4));
        view.Replay.SetSpeed(2);
        view.Replay.Play();
        Assert.True(view.Replay.IsPlaying);
        view.Replay.Pause();
        Assert.False(view.Replay.IsPlaying);
        Assert.False(view.StepReplay(1));
        Assert.True(view.SetReplayPoint(0));
        Assert.True(view.StepReplay(1));
        Assert.Equal(Fixture.ToUtc, view.Replay.CursorUtc);
        Assert.False(view.StepReplay(1));
        Assert.True(view.StepReplay(-1));
        Assert.Equal(Fixture.FromUtc, view.Replay.CursorUtc);
        Assert.False(view.StepReplay(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => view.StepReplay(0));
        view.ReturnReplayToPresent();
        Assert.Equal(Fixture.ToUtc, view.Replay.CursorUtc);
        Assert.True(view.Replay.IsAtPresent);
        Assert.False(view.Replay.IsPlaying);

        await view.RefreshAsync(null, Fixture.ToUtc, DiffMode.Live, TestContext.Current.CancellationToken);
        var callsBeforePause = source.Calls;
        view.PauseLive();
        await view.RefreshAsync(null, Fixture.ToUtc, DiffMode.Live, TestContext.Current.CancellationToken);
        Assert.Equal(callsBeforePause, source.Calls);
        Assert.True(view.IsPaused);
        await view.ResumeLiveAsync(TestContext.Current.CancellationToken);
        Assert.False(view.IsPaused);
        Assert.Equal(callsBeforePause + 1, source.Calls);
    }

    [Fact]
    public async Task ProjectionCancellationPropagatesAndMissingReplayTimelineIsAbsent()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceledView = new DiffViewModel(new CancelingProjectionSource());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await canceledView.RefreshAsync(null, Fixture.ToUtc, DiffMode.Live, cancellation.Token));

        var view = await Fixture.CreateViewAsync();
        Assert.True(view.NavigateToPath("/root/deleted.txt"));
        Assert.False(view.StepReplay(1));
        Assert.False(view.SetReplayPoint(0));
    }

    [Fact]
    public async Task ExplorerFolderPageReturnsDirectItemsAndVirtualGroupingFoldersOnly()
    {
        var original = Fixture.Projection(DiffMode.Period);
        var nested = original.Items[0] with
        {
            FileId = FileId.Create("deep-nested"),
            DisplayName = "deep.txt",
            OldPath = "/root/nested/deep.txt",
            NewPath = "/root/nested/deep.txt",
            DisplayPath = "/root/nested/deep.txt"
        };
        var projection = original with { Items = original.Items.Append(nested).ToArray() };
        var view = new DiffViewModel(new FixedProjectionSource(projection));
        await view.RefreshAsync(null, Fixture.ToUtc, DiffMode.Period, TestContext.Current.CancellationToken);

        var children = view.Explorer.GetChildrenPage("/root", 1, 10);
        Assert.Equal(RootFolderChildren, children.Select(row => row.Projection.DisplayName).Order(StringComparer.OrdinalIgnoreCase));
        var nestedFolder = Assert.Single(children, row => row.Projection.DisplayName == "nested");
        Assert.Equal(FileKind.Directory, nestedFolder.Projection.Kind);
        Assert.True(nestedFolder.Projection.IsVirtual);
        Assert.Equal("deep.txt", Assert.Single(view.Explorer.GetChildrenPage(nestedFolder.Projection.DisplayPath, 1, 10)).Projection.DisplayName);
        Assert.Empty(view.Explorer.GetChildrenPage("/root/current.txt", 1, 10));
    }

    private sealed class FakeProjectionSource : IDiffProjectionSource
    {
        public int Calls { get; private set; }
        public ValueTask<DiffProjection> GetDiffProjectionAsync(DiffProjectionQuery query, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(Fixture.Projection(query.Mode));
        }
    }

    private sealed class CancelingProjectionSource : IDiffProjectionSource
    {
        public ValueTask<DiffProjection> GetDiffProjectionAsync(DiffProjectionQuery query, CancellationToken cancellationToken = default) =>
            ValueTask.FromCanceled<DiffProjection>(cancellationToken);
    }

    private sealed class FixedProjectionSource(DiffProjection projection) : IDiffProjectionSource
    {
        public ValueTask<DiffProjection> GetDiffProjectionAsync(DiffProjectionQuery query, CancellationToken cancellationToken = default) => ValueTask.FromResult(projection);
    }

    private sealed class RecordingLauncher : IExplorerLauncher
    {
        public List<string> Paths { get; } = [];
        public ValueTask<ExplorerOpenResult> OpenAsync(string path, CancellationToken cancellationToken = default)
        {
            Paths.Add(path);
            return ValueTask.FromResult(new ExplorerOpenResult(true, null));
        }
    }

    private static class Fixture
    {
        public static readonly DateTimeOffset FromUtc = new(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
        public static readonly DateTimeOffset ToUtc = FromUtc.AddMinutes(10);

        public static async Task<DiffViewModel> CreateViewAsync(IExplorerLauncher? launcher = null)
        {
            var view = new DiffViewModel(new FakeProjectionSource(), launcher);
            await view.RefreshAsync(new DiffProjectionQuery(FromUtc, ToUtc, DiffMode.Replay, ProjectionFilter.Empty), TestContext.Current.CancellationToken);
            return view;
        }

        public static DiffProjection Projection(DiffMode mode)
        {
            var current = Item("current", "/root/current.txt", DiffPrimaryOperation.DataWrite, DiffSemanticState.Edited, false, true, null, mode == DiffMode.Replay ? Timeline() : Array.Empty<ReplayTimelinePoint>());
            var deleted = Item("deleted", "/root/deleted.txt", DiffPrimaryOperation.Delete, DiffSemanticState.Removed, true, false, null, Array.Empty<ReplayTimelinePoint>(), oldPath: "/root/deleted.txt", newPath: null);
            var moveFrom = Item("move-from", "/old/source.txt", DiffPrimaryOperation.MoveFrom, DiffSemanticState.Moved, false, true, "move-1", Array.Empty<ReplayTimelinePoint>(), oldPath: "/old/source.txt", newPath: "/old/source.txt");
            var moveTo = Item("move-to", "/new/destination.txt", DiffPrimaryOperation.MoveTo, DiffSemanticState.Moved, false, true, "move-1", Array.Empty<ReplayTimelinePoint>(), oldPath: "/old/source.txt", newPath: "/new/destination.txt");
            var unknown = Item(null, "Unknown location", DiffPrimaryOperation.Unknown, DiffSemanticState.Unknown, true, false, null, Array.Empty<ReplayTimelinePoint>(), oldPath: null, newPath: null, displayPathOverride: "Unknown location");
            var known = new[] { current, deleted, moveFrom, moveTo };
            return new(mode, ToUtc, known, new[] { unknown }, known.Select(ToEntry).Concat(new[] { ToEntry(unknown) }).ToArray());
        }

        private static FileDiffProjection Item(string? id, string displayPath, DiffPrimaryOperation operation, DiffSemanticState semantic, bool virtualRow, bool canOpen, string? related, IReadOnlyList<ReplayTimelinePoint> timeline, string? oldPath = null, string? newPath = null, string? displayPathOverride = null)
        {
            var path = newPath ?? displayPath;
            return new(id is null ? null : FileId.Create(id), Path.GetFileName(displayPath), oldPath ?? displayPath, newPath ?? path, displayPathOverride ?? displayPath, FileKind.File, EventQuality.Exact, operation, semantic, operation == DiffPrimaryOperation.DataWrite ? new[] { DiffPrimaryOperation.MetadataChange } : Array.Empty<DiffPrimaryOperation>(), related, virtualRow, operation is DiffPrimaryOperation.Delete or DiffPrimaryOperation.Recycle, false, canOpen, canOpen ? null : "This row is virtual or deleted.", 0, Array.Empty<string>(), timeline, new[] { EventId.New() });
        }

        private static ReplayTimelinePoint[] Timeline() => [new(EventId.New(), FromUtc, PaneLifecycle.Appeared, DiffPrimaryOperation.Create), new(EventId.New(), ToUtc, PaneLifecycle.Updated, DiffPrimaryOperation.DataWrite)];
        private static DiffEntry ToEntry(FileDiffProjection item) => new(item.FileId, item.OldPath, item.NewPath, item.PrimaryOperation == DiffPrimaryOperation.Delete ? CanonicalOperation.Delete : CanonicalOperation.DataWrite, item.Kind, item.Quality, item.IsVirtual);
    }
}
