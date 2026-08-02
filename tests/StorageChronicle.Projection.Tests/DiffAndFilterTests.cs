using System.Globalization;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Projection;

namespace StorageChronicle.Projection.Tests;

public sealed class DiffAndFilterTests
{
    [Fact]
    public void PeriodDiffKeepsCreateThenDeleteAndMakesDeletePrimary()
    {
        var create = ProjectionFixture.Event(0, CanonicalOperation.Create, "/root/temp.txt", "temp");
        var write = ProjectionFixture.Event(1, CanonicalOperation.DataWrite, "/root/temp.txt", "temp", size: 12);
        var delete = ProjectionFixture.Event(2, CanonicalOperation.Delete, "/root/temp.txt", "temp");
        var result = new DiffProjector().Project(ProjectionFixture.Document(create, write, delete), null, DateTimeOffset.Parse("2026-01-01T00:00:03Z", CultureInfo.InvariantCulture), DiffMode.Period, ProjectionFilter.Empty);

        var row = Assert.Single(result.Items);
        Assert.Equal(DiffPrimaryOperation.Delete, row.PrimaryOperation);
        Assert.True(row.IsPeriodOnly);
        Assert.Contains(DiffPrimaryOperation.DataWrite, row.SubOperations);
        Assert.True(row.IsVirtual);
        Assert.Contains(result.DomainEntries, entry => entry.Operation == CanonicalOperation.Delete);
    }

    [Fact]
    public void DiffPriorityAndMoveRelationReturnSemanticSubOperations()
    {
        var move = ProjectionFixture.Event(0, CanonicalOperation.Move, "/root/new.txt", "file", properties: [(ProjectionPropertyNames.OldPath, "/old/file.txt"), (ProjectionPropertyNames.RelatedOperationId, "move-1")]);
        var write = ProjectionFixture.Event(1, CanonicalOperation.DataWrite, "/root/new.txt", "file", size: 4);
        var share = ProjectionFixture.Event(2, CanonicalOperation.ShareChanged, "/root/new.txt", "file");
        var cloud = ProjectionFixture.Event(3, CanonicalOperation.CloudStateChanged, "/root/new.txt", "file");

        var row = Assert.Single(new DiffProjector().Project(ProjectionFixture.Document(move, write, share, cloud), null, DateTimeOffset.Parse("2026-01-01T00:00:04Z", CultureInfo.InvariantCulture), DiffMode.Replay, ProjectionFilter.Empty).Items);

        Assert.Equal(DiffPrimaryOperation.MoveTo, row.PrimaryOperation);
        Assert.Contains(DiffPrimaryOperation.MoveFrom, row.SubOperations);
        Assert.Contains(DiffPrimaryOperation.DataWrite, row.SubOperations);
        Assert.Contains(DiffPrimaryOperation.Share, row.SubOperations);
        Assert.Contains(DiffPrimaryOperation.CloudState, row.SubOperations);
        Assert.Equal("move-1", row.RelatedOperationId);
        Assert.Equal(4, row.ReplayTimeline.Count);
        Assert.Equal(PaneLifecycle.Updated, row.ReplayTimeline[^1].Lifecycle);
    }

    [Fact]
    public void UnknownLocationUsesVirtualRootAndCannotOpen()
    {
        var value = ProjectionFixture.Event(0, CanonicalOperation.Create, null, "unknown");
        var result = new DiffProjector().Project(ProjectionFixture.Document(value), null, DateTimeOffset.Parse("2026-01-01T00:00:01Z", CultureInfo.InvariantCulture), DiffMode.PointInTime, ProjectionFilter.Empty);

        var row = Assert.Single(result.UnknownLocationItems);
        Assert.Equal("場所を特定できない項目", row.DisplayPath);
        Assert.False(row.CanOpenInExplorer);
        Assert.NotNull(row.OpenDisabledReason);
    }

    [Fact]
    public async Task LiteralAndOrAndExclusionFiltersAreComposable()
    {
        var alpha = ProjectionFixture.Event(0, CanonicalOperation.Create, "/root/alpha.txt", "a");
        var beta = ProjectionFixture.Event(1, CanonicalOperation.DataWrite, "/root/beta.txt", "b");
        var gamma = ProjectionFixture.Event(2, CanonicalOperation.Delete, "/root/gamma.txt", "c");
        var service = new ProjectionService(ProjectionFixture.Document(alpha, beta, gamma));
        var filter = new ProjectionFilter(
            [new FilterTerm(FilterField.Path, "/root")],
            [new FilterTerm(FilterField.Operation, nameof(CanonicalOperation.Create)), new FilterTerm(FilterField.Operation, nameof(CanonicalOperation.Delete))],
            [new FilterTerm(FilterField.Name, "gamma")]);

        var page = await service.GetEventStackAsync(new EventStackQuery(EventStackMode.Normalized, 1, 10, filter: filter), TestContext.Current.CancellationToken);

        Assert.Single(page.Items);
        Assert.Equal(alpha.EventId, page.Items[0].Id);
        Assert.Throws<NotSupportedException>(() => ProjectionFilterEvaluator.Matches(new ProjectionFilter(All: [new FilterTerm(FilterField.Name, "x", FilterMatchKind.Regex)]), new ProjectionFilterContext(new Dictionary<FilterField, string>())));
    }
}
