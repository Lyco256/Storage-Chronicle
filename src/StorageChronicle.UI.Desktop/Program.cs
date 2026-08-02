using Avalonia;

namespace StorageChronicle.UI.Desktop;

/// <summary>Starts the cross-platform desktop shell without owning platform collectors.</summary>
public static class Program
{
    /// <summary>Desktop application entry point.</summary>
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>Creates the Avalonia application builder.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
