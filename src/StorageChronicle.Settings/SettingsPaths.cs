namespace StorageChronicle.Settings;

/// <summary>Resolves storage paths without making the settings model platform-specific.</summary>
public interface ISettingsPathProvider
{
    /// <summary>Gets the machine settings path.</summary>
    string MachineSettingsPath { get; }
    /// <summary>Gets the user settings path.</summary>
    string UserSettingsPath { get; }
}
/// <summary>Resolves the Windows paths specified by the product requirements.</summary>
public sealed class WindowsSettingsPathProvider : ISettingsPathProvider
{
    /// <summary>Initializes a path provider using Windows special folders.</summary>
    public WindowsSettingsPathProvider()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
    {
    }

    /// <summary>Initializes a path provider with explicit roots, primarily for tests.</summary>
    public WindowsSettingsPathProvider(string programDataRoot, string localApplicationDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programDataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataRoot);
        MachineSettingsPath = Path.Combine(programDataRoot, "Storage Chronicle", "config", "machine-settings.json");
        UserSettingsPath = Path.Combine(localApplicationDataRoot, "Storage Chronicle", "user-settings.json");
    }

    /// <inheritdoc />
    public string MachineSettingsPath { get; }
    /// <inheritdoc />
    public string UserSettingsPath { get; }
}

