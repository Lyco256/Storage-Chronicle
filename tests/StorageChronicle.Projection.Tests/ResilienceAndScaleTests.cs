using System.Globalization;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Projection;

namespace StorageChronicle.Projection.Tests;

public sealed class ResilienceAndScaleTests
{
    [Fact]
    public async Task CancellationIsHonoredBeforeProjectionWork()
    {
        var service = new ProjectionService(ProjectionFixture.Document(ProjectionFixture.Event(0, CanonicalOperation.Create, "/root/a", "a")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await service.GetEventStackAsync(EventStackMode.Grouped, 1, 50, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await service.GetDiffAsync(null, DateTimeOffset.Parse("2026-01-01T00:00:01Z", CultureInfo.InvariantCulture), DiffMode.Live, cancellation.Token));
    }

    [Fact]
    public void LargeFixtureRegeneratesWithoutChangingEventIds()
    {
        var document = ProjectionFixture.LargeDocument(100_000);
        var before = document.CanonicalEvents.Select(value => value.EventId).ToArray();
        var groups = new ActivityGrouper().Group(document.CanonicalEvents, TimeSpan.FromSeconds(2));
        var after = document.CanonicalEvents.Select(value => value.EventId).ToArray();

        Assert.Equal(100_000, groups.Count);
        Assert.Equal(before, after);
        Assert.Equal(100_000, groups.Sum(group => group.Metrics.OperationCount));
    }
}
