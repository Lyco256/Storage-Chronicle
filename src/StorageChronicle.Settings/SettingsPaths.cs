using System.Globalization;

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
        UserSettingsPath = GetUserSettingsPath(localApplicationDataRoot);
    }

    /// <summary>Derives the user-settings path from a SID authenticated by the Agent and that user's OS-resolved LocalAppData root.</summary>
    /// <param name="authenticatedUserSid">The SID obtained from the authenticated Windows client token, never from an IPC payload.</param>
    /// <param name="trustedLocalApplicationDataRoot">The matching profile's LocalAppData path resolved by the privileged host.</param>
    /// <returns>A path provider rooted in the supplied authenticated user's profile.</returns>
    /// <exception cref="ArgumentException">The SID is malformed or the profile path is not a fully-qualified local path.</exception>
    /// <remarks>The caller is responsible for authenticating the SID and resolving the matching profile root. This API deliberately accepts no client payload or target filename.</remarks>
    public static WindowsSettingsPathProvider ForAuthenticatedUser(string authenticatedUserSid, string trustedLocalApplicationDataRoot)
    {
        ValidateWindowsSid(authenticatedUserSid);
        ValidateLocalApplicationDataRoot(trustedLocalApplicationDataRoot);
        return new WindowsSettingsPathProvider(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            trustedLocalApplicationDataRoot);
    }

    /// <inheritdoc />
    public string MachineSettingsPath { get; }
    /// <inheritdoc />
    public string UserSettingsPath { get; }

    private static string GetUserSettingsPath(string localApplicationDataRoot)
    {
        ValidateLocalApplicationDataRoot(localApplicationDataRoot);
        return Path.GetFullPath(Path.Combine(localApplicationDataRoot, "Storage Chronicle", "user-settings.json"));
    }

    private static void ValidateLocalApplicationDataRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("LocalAppData must be a fully-qualified local filesystem path.", nameof(path));
        }

        var segments = path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("LocalAppData must not contain relative path segments.", nameof(path));
        }
    }

    private static void ValidateWindowsSid(string sid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        var components = sid.Split('-');
        if (components.Length is < 4 or > 18 || components[0] != "S" || components[1] != "1" ||
            !ulong.TryParse(components[2], NumberStyles.None, CultureInfo.InvariantCulture, out var identifierAuthority) || identifierAuthority > 0x0000_FFFF_FFFF_FFFF ||
            components.Skip(3).Any(component => !uint.TryParse(component, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            throw new ArgumentException("An authenticated Windows SID in canonical S-1-numeric form is required.", nameof(sid));
        }
    }
}

