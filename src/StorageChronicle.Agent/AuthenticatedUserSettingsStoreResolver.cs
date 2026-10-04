using Microsoft.Win32;
using System.Security.Principal;
using StorageChronicle.Settings;

namespace StorageChronicle.Agent;

/// <summary>Resolves a settings store using an SID authenticated from the named-pipe client token.</summary>
public interface IAuthenticatedUserSettingsStoreResolver
{
    /// <summary>Returns the user-settings store for the authenticated SID, or fails closed when its profile is unavailable.</summary>
    /// <param name="authenticatedSid">The SID obtained from the authenticated named-pipe client token.</param>
    /// <returns>The fixed product settings store for that user's OS-resolved profile.</returns>
    ISettingsStore<UserSettings> Resolve(string authenticatedSid);
}

/// <summary>Resolves LocalAppData from the Windows profile registered for a user SID.</summary>
public interface IWindowsUserProfileResolver
{
    /// <summary>Returns the LocalAppData path for a SID using read-only operating-system profile data.</summary>
    /// <param name="authenticatedSid">The SID obtained from the authenticated named-pipe client token.</param>
    /// <returns>The matching Windows profile's LocalAppData directory.</returns>
    string ResolveLocalAppData(string authenticatedSid);
}

/// <summary>Uses Windows ProfileList registry data to resolve the matching user's local profile.</summary>
public sealed class WindowsUserProfileResolver : IWindowsUserProfileResolver
{
    /// <inheritdoc />
    /// <exception cref="ArgumentException">The SID is not a valid Windows SID.</exception>
    /// <exception cref="DirectoryNotFoundException">The SID has no registered profile or LocalAppData directory.</exception>
    /// <exception cref="IOException">The registry profile path is not a resolvable local path.</exception>
    public string ResolveLocalAppData(string authenticatedSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticatedSid);
        var sid = new SecurityIdentifier(authenticatedSid).Value;
        using var profileList = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}", writable: false);
        var profilePath = profileList?.GetValue("ProfileImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (string.IsNullOrWhiteSpace(profilePath)) throw new DirectoryNotFoundException("The authenticated user's Windows profile is not registered.");

        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive");
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(systemDrive) || string.IsNullOrWhiteSpace(windowsDirectory))
            throw new IOException("Windows profile environment paths are unavailable.");
        var fullProfilePath = Path.GetFullPath(ExpandSystemVariables(profilePath, systemDrive, windowsDirectory));
        if (!Path.IsPathFullyQualified(fullProfilePath) || fullProfilePath.StartsWith("\\\\", StringComparison.Ordinal))
            throw new IOException("The user's profile is not on a local filesystem path.");

        using var userShellFolders = Registry.Users.OpenSubKey($@"{sid}\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", writable: false);
        var localAppDataValue = userShellFolders?.GetValue("Local AppData", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (string.IsNullOrWhiteSpace(localAppDataValue)) throw new DirectoryNotFoundException("The authenticated user's LocalAppData mapping is unavailable.");
        var userExpandedLocalAppData = localAppDataValue.Replace("%USERPROFILE%", fullProfilePath, StringComparison.OrdinalIgnoreCase);
        var localAppData = Path.GetFullPath(ExpandSystemVariables(userExpandedLocalAppData, systemDrive, windowsDirectory));
        if (!Path.IsPathFullyQualified(localAppData) || localAppData.StartsWith("\\\\", StringComparison.Ordinal))
            throw new IOException("The authenticated user's LocalAppData path is not local.");
        if (!Directory.Exists(localAppData)) throw new DirectoryNotFoundException("The authenticated user's LocalAppData profile directory is unavailable.");
        return localAppData;
    }

    private static string ExpandSystemVariables(string value, string systemDrive, string windowsDirectory)
    {
        var expanded = value
            .Replace("%SystemDrive%", systemDrive, StringComparison.OrdinalIgnoreCase)
            .Replace("%SystemRoot%", windowsDirectory, StringComparison.OrdinalIgnoreCase);
        if (expanded.Contains('%')) throw new IOException("The user's profile path contains an unresolved environment variable.");
        return expanded;
    }
}

/// <summary>Creates fixed-path user stores using authenticated SID and freshly resolved Windows profile data.</summary>
public sealed class WindowsAuthenticatedUserSettingsStoreResolver : IAuthenticatedUserSettingsStoreResolver
{
    private readonly IWindowsUserProfileResolver profileResolver;

    /// <summary>Initializes the resolver with the operating-system profile lookup.</summary>
    public WindowsAuthenticatedUserSettingsStoreResolver(IWindowsUserProfileResolver profileResolver)
    {
        this.profileResolver = profileResolver ?? throw new ArgumentNullException(nameof(profileResolver));
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The SID is invalid.</exception>
    /// <exception cref="DirectoryNotFoundException">The SID has no available user profile.</exception>
    public ISettingsStore<UserSettings> Resolve(string authenticatedSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticatedSid);
        var localAppData = profileResolver.ResolveLocalAppData(authenticatedSid);
        return UserSettingsStore.ForAuthenticatedUser(authenticatedSid, localAppData);
    }
}
