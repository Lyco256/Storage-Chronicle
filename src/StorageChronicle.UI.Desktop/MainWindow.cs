using Avalonia.Automation;
using Avalonia.Controls;
using StorageChronicle.UI.EventStack;
using StorageChronicle.UI.Settings;
using StorageChronicle.UI.Shared;

namespace StorageChronicle.UI.Desktop;

/// <summary>Top-level shell; feature screens are registered behind UI-neutral contracts.</summary>
public sealed class MainWindow : Window
{
    /// <summary>Creates the navigation shell.</summary>
    public MainWindow()
    {
        Title = "Storage Chronicle";
        Width = 1280;
        Height = 800;
        var client = new AgentPipeProjectionClient();
        var eventStack = new EventStackView(new EventStackViewModel(new AgentEventStackProjection(client)));
        var diff = new DiffProjectionPanel(client);
        var health = new AgentHealthPanel(client);
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Event Stack", Content = eventStack });
        tabs.Items.Add(new TabItem { Header = "Diff View", Content = diff });
        var settings = new Button { Content = "Settings", [AutomationProperties.NameProperty] = "Open settings dialog" };
        settings.Click += async (_, _) => await OpenSettingsAsync(client).ConfigureAwait(true);
        var topBar = new DockPanel
        {
            Children =
            {
                new Border { [DockPanel.DockProperty] = Dock.Left, Child = settings },
                new Border { [DockPanel.DockProperty] = Dock.Right, Child = health }
            }
        };
        Content = new DockPanel { Children = { new Border { [DockPanel.DockProperty] = Dock.Top, Child = topBar }, tabs } };
    }

    private async Task OpenSettingsAsync(AgentPipeProjectionClient client)
    {
        try
        {
            var dialog = new SettingsDialogWindow(new AgentPipeSettingsGateway(client));
            await dialog.ShowDialogAsync(this).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or InvalidDataException)
        {
            Title = $"Storage Chronicle — settings unavailable: {exception.Message}";
        }
    }
}
