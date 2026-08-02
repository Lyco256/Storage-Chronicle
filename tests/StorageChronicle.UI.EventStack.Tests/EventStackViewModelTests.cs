using StorageChronicle.Domain.Contracts;
using StorageChronicle.UI.EventStack;
using StorageChronicle.UI.Shared;
using Xunit;

namespace StorageChronicle.UI.EventStack.Tests;

public sealed class EventStackViewModelTests
{
    [Fact]
    public async Task ModesPagingAndLiveFollowWorkWithFakeProjection()
    {
        var source = new FakePageSource();
        var view = new EventStackViewModel(source);
        await view.LoadPageAsync(1, 50);
        Assert.Single(view.Rows);
        view.StopFollowing();
        Assert.False(view.IsFollowing);
        await view.SetModeAsync(EventStackMode.Source);
        Assert.Equal(EventStackMode.Source, view.Mode);
        await view.ReturnToCurrentAsync();
        Assert.True(view.IsFollowing);
    }
    private sealed class FakePageSource : IVirtualizedPageSource<EventStackRow>
    {
        public ValueTask<ProjectionPage<EventStackRow>> GetPageAsync(int page, int pageSize, CancellationToken cancellationToken = default) => ValueTask.FromResult(new ProjectionPage<EventStackRow>([new EventStackRow(EventId.New(), DateTimeOffset.UtcNow, "root", CanonicalOperation.Create, "created", EventQuality.Exact, null, ProcessAttributionQuality.Unknown, EventOrigin.LiveUsn, [])], page, pageSize, 1, false));
    }
}
