using StorageChronicle.Domain.Contracts;
using StorageChronicle.Projection;

namespace StorageChronicle.Projection.Tests;

public sealed class EventStackProjectionTests
{
    [Fact]
    public async Task BuildsThreeModesAndKeepsGroupedDescendantsUnderOneRoot()
    {
        var first = ProjectionFixture.Event(0, CanonicalOperation.Create, "/root/a.txt", "a");
        var second = ProjectionFixture.Event(1, CanonicalOperation.DataWrite, "/root/a.txt", "a", properties: [(ProjectionPropertyNames.NormalizedEventId, first.EventId.Value.ToString())]);
        var document = ProjectionFixture.Document(first, second);
        var service = new ProjectionService(document);

        var source = await service.GetEventStackTreeAsync(new EventStackQuery(EventStackMode.Source, 1, 2), TestContext.Current.CancellationToken);
        var normalized = await service.GetEventStackTreeAsync(new EventStackQuery(EventStackMode.Normalized, 1, 2), TestContext.Current.CancellationToken);
        var grouped = await service.GetEventStackTreeAsync(new EventStackQuery(EventStackMode.Grouped, 1, 1), TestContext.Current.CancellationToken);

        Assert.Equal(2, source.Items.Count);
        Assert.Equal(2, normalized.Items.Count);
        var groupedRoot = Assert.Single(grouped.Items);
        Assert.Equal(2, groupedRoot.Row.Children.Count);
        Assert.NotEmpty(groupedRoot.Children);
        Assert.Contains(groupedRoot.Children, node => node.Kind == EventStackNodeKind.FileSummary);
        Assert.Equal(2, groupedRoot.Children.SelectMany(node => node.Children).SelectMany(node => node.Children).Count());
    }

    [Fact]
    public async Task SortsNewestFirstAndPagesRootActivitiesWithoutSplittingChildren()
    {
        var events = Enumerable.Range(0, 3)
            .Select(index => ProjectionFixture.Event(index * 10, CanonicalOperation.Create, $"/root/{index}.txt", index.ToString(System.Globalization.CultureInfo.InvariantCulture), $"process-{index}"))
            .ToArray();
        var service = new ProjectionService(ProjectionFixture.Document(events));

        var descending = await service.GetEventStackTreeAsync(new EventStackQuery(EventStackMode.Grouped, 1, 2), TestContext.Current.CancellationToken);
        var ascending = await service.GetEventStackTreeAsync(new EventStackQuery(EventStackMode.Grouped, 1, 2, ProjectionSortOrder.Ascending), TestContext.Current.CancellationToken);
        var secondPage = await service.GetEventStackTreeAsync(new EventStackQuery(EventStackMode.Grouped, 2, 2), TestContext.Current.CancellationToken);

        Assert.Equal(3, descending.TotalCount);
        Assert.Equal(2, descending.Items.Count);
        Assert.Equal(events[2].EventId, descending.Items[0].Row.Id);
        Assert.Equal(events[0].EventId, ascending.Items[0].Row.Id);
        Assert.Single(secondPage.Items);
        Assert.NotEmpty(descending.Items[0].Children);
    }

    [Fact]
    public void GroupsUnknownOnlyWithinSourceVolumeMountRouteAndTimeout()
    {
        var first = ProjectionFixture.Event(0, CanonicalOperation.Create, "/root/a.txt", "a", null, ProcessAttributionQuality.Unknown);
        var same = ProjectionFixture.Event(1, CanonicalOperation.DataWrite, "/root/a.txt", "a", null, ProcessAttributionQuality.Unknown);
        var otherMount = ProjectionFixture.Event(1, CanonicalOperation.DataWrite, "/root/a.txt", "b", null, ProcessAttributionQuality.Unknown, mount: "mount-2");
        var late = ProjectionFixture.Event(4, CanonicalOperation.DataWrite, "/root/a.txt", "c", null, ProcessAttributionQuality.Unknown);

        var groups = new ActivityGrouper().Group([first, same, otherMount, late], TimeSpan.FromSeconds(2));

        Assert.Equal(3, groups.Count);
        Assert.Equal(2, groups[0].Metrics.OperationCount);
        Assert.Equal("不明なプロセス", groups[0].ProcessDisplayName);
        Assert.NotEqual(groups[0].MountSession, groups[1].MountSession);
    }

    [Fact]
    public void ReadObservationDoesNotSplitAnActivityButTimeoutDoes()
    {
        var first = ProjectionFixture.Event(0, CanonicalOperation.Create, "/root/a.txt", "a");
        var read = ProjectionFixture.Event(1, CanonicalOperation.MetadataChanged, "/root/a.txt", "a", origin: EventOrigin.Etw, properties: [("observation", "Read")]);
        var second = ProjectionFixture.Event(1, CanonicalOperation.DataWrite, "/root/a.txt", "a");
        var late = ProjectionFixture.Event(4, CanonicalOperation.DataWrite, "/root/a.txt", "a");

        var groups = new ActivityGrouper().Group([first, read, second, late], TimeSpan.FromSeconds(2));

        Assert.Equal(2, groups.Count);
        Assert.Equal(3, groups[0].Metrics.OperationCount);
        Assert.Equal(1, groups[1].Metrics.OperationCount);
    }

    [Fact]
    public void DisplaysNewFolderAsRouteOnlyWhenItsDescendantsAreTheActivity()
    {
        var createFolder = ProjectionFixture.Event(0, CanonicalOperation.DirectoryCreate, "/root/new", "folder", kind: FileKind.Directory);
        var child = ProjectionFixture.Event(1, CanonicalOperation.Create, "/root/new/child.txt", "child");
        var emptyFolder = ProjectionFixture.Event(10, CanonicalOperation.DirectoryCreate, "/root/empty", "empty", kind: FileKind.Directory, processId: "p2");

        var groups = new ActivityGrouper().Group([createFolder, child, emptyFolder], TimeSpan.FromSeconds(2));

        Assert.Equal("/root/new", groups[0].DisplayRoute);
        Assert.Equal("/root", groups[1].DisplayRoute);
    }

    [Fact]
    public void CompetingProcessCreatesANewActivityWhenItTouchesTheCurrentRoute()
    {
        var first = ProjectionFixture.Event(0, CanonicalOperation.DataWrite, "/root/a/one.txt", "one", "p1");
        var competing = ProjectionFixture.Event(1, CanonicalOperation.DataWrite, "/root/a/two.txt", "two", "p2");
        var resumed = ProjectionFixture.Event(2, CanonicalOperation.DataWrite, "/root/a/one.txt", "one", "p1");

        var groups = new ActivityGrouper().Group([first, competing, resumed], TimeSpan.FromSeconds(2));

        Assert.Equal(3, groups.Count);
        Assert.True(groups[0].IsClosedByCompetingActivity);
        Assert.Equal("p1", groups[2].Process?.Id?.Value);
    }

    [Fact]
    public void UnverifiedGapIsAnIndependentExpandableRoot()
    {
        var before = ProjectionFixture.Event(0, CanonicalOperation.DataWrite, "/root/a.txt", "a");
        var gap = ProjectionFixture.Event(1, CanonicalOperation.UnverifiedGap, "/root/a.txt", "a", properties: [("gap", "journal discontinuity")]);
        var after = ProjectionFixture.Event(2, CanonicalOperation.DataWrite, "/root/a.txt", "a");

        var groups = new EventStackProjector().GetTreePage(
            ProjectionFixture.Document(before, gap, after),
            new EventStackQuery(EventStackMode.Grouped, 1, 10),
            new ProjectionSettings()).Items;

        Assert.Equal(3, groups.Count);
        Assert.Contains(groups, node => node.Kind == EventStackNodeKind.Gap);
    }
}
