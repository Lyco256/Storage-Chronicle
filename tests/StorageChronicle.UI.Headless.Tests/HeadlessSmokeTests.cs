using Avalonia;
using Avalonia.Controls;
using StorageChronicle.UI.EventStack;
using StorageChronicle.UI.Shared;
using StorageChronicle.Domain.Contracts;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using StorageChronicle.UI.Desktop;
using StorageChronicle.Contracts.Runtime;
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

    [AvaloniaFact]
    public async Task ReconciliationConfirmationUsesFixedWarningAndExplicitChoices()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var request = new PendingReconciliationRequest(
                "gap-1",
                VolumeId.Create("\\\\?\\Volume{TEST}\\"),
                "USN journal history is unavailable",
                42,
                DateTimeOffset.UtcNow,
                FileSystem: "NTFS",
                GapStartUtc: DateTimeOffset.UtcNow.AddMinutes(-5));
            var dialog = new ReconciliationConfirmationWindow(request);
            var dialogTask = dialog.ShowDialogAsync(owner);
            dialog.Measure(new Size(640, 420));
            dialog.Arrange(new Rect(0, 0, 640, 420));
            await Task.Yield();

            var buttons = dialog.GetVisualDescendants().OfType<Button>().ToArray();
            var execute = buttons.SingleOrDefault(value => value.Content as string == "実行する");
            var decline = buttons.SingleOrDefault(value => value.Content as string == "実行しない");
            Assert.NotNull(execute);
            Assert.NotNull(decline);
            Assert.Equal("実行する", execute!.Content);
            Assert.Equal("実行しない", decline!.Content);
            Assert.Contains("このドライブの変更履歴に欠落があります", FindText(dialog.Content));

            decline.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.False(await dialogTask.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            owner.Close();
        }
    }

    private static string FindText(object? value)
    {
        if (value is TextBlock text) return text.Text ?? string.Empty;
        if (value is Panel panel) return string.Join("\n", panel.Children.Select(FindText));
        return string.Empty;
    }

    private sealed class StubSource : IVirtualizedPageSource<EventStackRow>
    {
        public ValueTask<ProjectionPage<EventStackRow>> GetPageAsync(int page, int pageSize, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ProjectionPage<EventStackRow>(Array.Empty<EventStackRow>(), page, pageSize, 0, false));
    }
}
