using StorageChronicle.UI.EventStack;
using StorageChronicle.UI.Shared;
using StorageChronicle.Domain.Contracts;
using Avalonia.Headless.XUnit;
using StorageChronicle.UI.Desktop;
using Xunit;

namespace StorageChronicle.UI.Headless.Tests;

public sealed class HeadlessSmokeTests
{
    [Fact]
    public async Task EventStackViewModelLoadsWithoutUiThreadIo()
    {
        var source = new StubSource();
        var viewModel = new EventStackViewModel(source);
        await viewModel.LoadPageAsync(1, 50);
        Assert.Equal(EventStackMode.Grouped, viewModel.Mode);
    }

    [AvaloniaFact]
    public void DesktopShellStartsAndRendersItsNavigationSurface()
    {
        var window = new MainWindow();
        window.Show();
        try
        {
            Assert.Equal("Storage Chronicle", window.Title);
            Assert.NotNull(window.Content);
            Assert.True(window.Width >= 1280);
            Assert.True(window.Height >= 800);
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class StubSource : IVirtualizedPageSource<EventStackRow>
    {
        public ValueTask<ProjectionPage<EventStackRow>> GetPageAsync(int page, int pageSize, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ProjectionPage<EventStackRow>(Array.Empty<EventStackRow>(), page, pageSize, 0, false));
    }
}
