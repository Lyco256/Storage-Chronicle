using StorageChronicle.Settings;

namespace StorageChronicle.Agent;

/// <summary>Prevents a LocalSystem service from falling back to its own profile for interactive User Settings.</summary>
internal sealed class UnscopedUserSettingsStore : ISettingsStore<UserSettings>
{
    /// <inheritdoc />
    public SettingsLoadResult<UserSettings> Load() => throw new InvalidOperationException("Interactive User Settings require an authenticated user's profile store.");

    /// <inheritdoc />
    public void Save(UserSettings settings) => throw new InvalidOperationException("Interactive User Settings require an authenticated user's profile store.");
}
