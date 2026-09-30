using StorageChronicle.Settings;

namespace StorageChronicle.UI.Shared;

/// <summary>Loads and applies current-user preferences through the Agent IPC boundary.</summary>
public interface IUserSettingsClient
{
    /// <summary>Loads the current user's validated settings snapshot.</summary>
    ValueTask<UserSettings> LoadUserSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies a validated user-settings snapshot through the Agent.</summary>
    ValueTask<SettingsApplyResult> ApplyUserSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default);
}
