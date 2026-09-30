using Avalonia;
using Avalonia.Controls;
using StorageChronicle.UI.EventStack;
using StorageChronicle.UI.DiffView;
using StorageChronicle.UI.Shared;
using StorageChronicle.Domain.Contracts;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using StorageChronicle.UI.Desktop;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.Contracts;
using StorageChronicle.Settings;
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
    public async Task DiffViewExposesRequiredLayoutsZoomNavigationAndReplayControls()
    {
        var panel = new DiffProjectionPanel(new DiffStubProjection());
        var window = new Window { Content = panel, Width = 1280, Height = 800 };
        window.Show();
        try
        {
            await Task.Delay(50);
            var names = panel.GetVisualDescendants()
                .OfType<Control>()
                .Select(Avalonia.Automation.AutomationProperties.GetName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
            foreach (var mode in Enum.GetNames<ExplorerViewMode>())
            {
                Assert.Contains($"Explorer layout {mode}", names);
            }

            Assert.Contains("Explorer icon zoom", names);
            Assert.Contains("Diff navigation back", names);
            Assert.Contains("Diff navigation forward", names);
            Assert.Contains("Diff Explorer path", names);
            Assert.Contains("Navigate to parent Diff folder", names);
            Assert.Contains("Replay event timeline", names);
            Assert.Contains("Replay previous event", names);
            Assert.Contains("Replay next event", names);
            Assert.Contains("Replay speed", names);
            Assert.Contains("Pause or resume Diff Live updates", names);
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

    private sealed class DiffStubProjection : IProjectionService
    {
        public ValueTask<ProjectionPage<EventStackRow>> GetEventStackAsync(EventStackMode mode, int page, int pageSize, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ProjectionPage<EventStackRow>(Array.Empty<EventStackRow>(), page, pageSize, 0, false));

        public ValueTask<IReadOnlyList<DiffEntry>> GetDiffAsync(DateTimeOffset? fromUtc, DateTimeOffset toUtc, DiffMode mode, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<DiffEntry>>(
            [new DiffEntry(FileId.Create("diff-ui-test-file"), null, @"C:\\fixture\\new.txt", CanonicalOperation.Create, FileKind.File, EventQuality.Exact, IsVirtual: false)]);
    }
}
