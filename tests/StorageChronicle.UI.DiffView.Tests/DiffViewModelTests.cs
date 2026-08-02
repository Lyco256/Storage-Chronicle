using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.UI.DiffView;
using Xunit;

namespace StorageChronicle.UI.DiffView.Tests;

public sealed class DiffViewModelTests
{
    [Fact]
    public async Task AllExplorerModesShareOneProjectionAndPauseOnlyUI()
    {
        var projection = new FakeProjection();
        var view = new DiffViewModel(projection);
        await view.RefreshAsync(null, DateTimeOffset.UtcNow, DiffMode.Period);
        Assert.Single(view.Rows);
        view.SetViewMode(ExplorerViewMode.Content);
        view.PauseLive();
        Assert.True(view.IsPaused);
        view.ResumeLive();
        Assert.False(view.IsPaused);
    }
    private sealed class FakeProjection : IProjectionService
    {
        public ValueTask<ProjectionPage<EventStackRow>> GetEventStackAsync(EventStackMode mode, int page, int pageSize, CancellationToken cancellationToken = default) => ValueTask.FromResult(new ProjectionPage<EventStackRow>([], page, pageSize, 0, false));
        public ValueTask<IReadOnlyList<DiffEntry>> GetDiffAsync(DateTimeOffset? fromUtc, DateTimeOffset toUtc, DiffMode mode, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<DiffEntry>>([new DiffEntry(null, null, "f", CanonicalOperation.Create, FileKind.File, EventQuality.Exact, false)]);
    }
}
