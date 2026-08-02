using Avalonia.Controls;

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
        Content = new TextBlock { Text = "Storage Chronicle — Event Stack · Diff View", Margin = new Avalonia.Thickness(24) };
    }
}
