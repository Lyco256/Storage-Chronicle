using Avalonia.Controls;
using StorageChronicle.UI.EventStack;
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
        var eventStack = new EventStackView(new EventStackViewModel(new AgentEventStackProjection(client), settingsClient: client));
        var diff = new DiffProjectionPanel(client);
        var health = new AgentHealthPanel(client);
        var settings = new Button { Content = "Settings", [Avalonia.Automation.AutomationProperties.NameProperty] = "Open settings" };
        settings.Click += async (_, _) => await new SettingsDialogWindow(client).ShowDialog(this).ConfigureAwait(true);
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Event Stack", Content = eventStack });
        tabs.Items.Add(new TabItem { Header = "Diff View", Content = diff });
        Content = new DockPanel { Children =
        {
            new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Children =
            {
                new Border { [DockPanel.DockProperty] = Dock.Top, Child = health }, settings
            } },
            tabs
        } };
    }
}
