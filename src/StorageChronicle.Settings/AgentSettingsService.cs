namespace StorageChronicle.Settings;

/// <summary>Represents the IPC-facing agent boundary used by the settings dialog.</summary>
public interface IAgentSettingsGateway
{
    /// <summary>Loads machine settings through the agent.</summary>
    SettingsLoadResult<MachineSettings> LoadMachineSettings();
    /// <summary>Loads user settings through the agent.</summary>
    SettingsLoadResult<UserSettings> LoadUserSettings();
    /// <summary>Validates, persists, and applies machine settings through the agent.</summary>
    ValueTask<SettingsApplyResult> ApplyMachineSettingsAsync(MachineSettings settings, CancellationToken cancellationToken = default);
    /// <summary>Validates and persists user settings through the agent.</summary>
    ValueTask<SettingsApplyResult> ApplyUserSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>Authorizes machine-setting changes before they reach persistence.</summary>
public interface IAgentSettingsAuthorizer
{
    /// <summary>Returns whether the caller may apply the proposed machine settings.</summary>
    bool CanApplyMachineSettings(MachineSettings settings);
}

/// <summary>Records a settings history fact after a successful application.</summary>
public interface ISettingsChangeHistory
{
    /// <summary>Records scope, timestamp, and changed property names only.</summary>
    ValueTask RecordAsync(SettingsChangeHistoryEvent value, CancellationToken cancellationToken = default);
}

/// <summary>Performs the safe monitoring stop/flush/restart required by a machine change.</summary>
public interface IMonitoringLifecycle
{
    /// <summary>Safely stops monitoring, flushes durable data, and starts it again.</summary>
    ValueTask RestartAsync(CancellationToken cancellationToken = default);
}

/// <summary>Implements the settings Agent endpoint independently from UI file access.</summary>
public sealed class AgentSettingsService : IAgentSettingsGateway
{
    private readonly ISettingsStore<MachineSettings> machineStore;
    private readonly ISettingsStore<UserSettings> userStore;
    private readonly ISettingsChangeHistory history;
    private readonly IAgentSettingsAuthorizer authorizer;
    private readonly IMonitoringLifecycle lifecycle;
    private readonly Func<DateTimeOffset> clock;

    /// <summary>Initializes the Agent settings service.</summary>
    public AgentSettingsService(
        ISettingsStore<MachineSettings> machineStore,
        ISettingsStore<UserSettings> userStore,
        ISettingsChangeHistory history,
        IAgentSettingsAuthorizer authorizer,
        IMonitoringLifecycle lifecycle,
        Func<DateTimeOffset>? clock = null)
    {
        this.machineStore = machineStore;
        this.userStore = userStore;
        this.history = history;
        this.authorizer = authorizer;
        this.lifecycle = lifecycle;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public SettingsLoadResult<MachineSettings> LoadMachineSettings() => machineStore.Load();

    /// <inheritdoc />
    public SettingsLoadResult<UserSettings> LoadUserSettings() => userStore.Load();

    /// <inheritdoc />
    public async ValueTask<SettingsApplyResult> ApplyMachineSettingsAsync(MachineSettings settings, CancellationToken cancellationToken = default)
    {
        var validation = SettingsValidator.Validate(settings);
        if (!validation.IsValid)
        {
            return SettingsApplyResult.Failed(string.Join("; ", validation.Errors.Select(error => $"{error.Property}: {error.Message}")));
        }

        if (!authorizer.CanApplyMachineSettings(settings))
        {
            return SettingsApplyResult.Failed("The agent rejected this machine settings change.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var previous = machineStore.Load().Settings;
        machineStore.Save(settings);
        var changedProperties = ChangedMachineProperties(previous, settings);
        var restartRequired = changedProperties.Count > 0;
        if (restartRequired)
        {
            try
            {
                await lifecycle.RestartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                RestoreMachineSettings(previous);
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                RestoreMachineSettings(previous);
                return SettingsApplyResult.Failed($"Monitoring restart failed: {exception.Message}");
            }
        }

        await history.RecordAsync(new("Machine", clock(), changedProperties), cancellationToken).ConfigureAwait(false);
        return new(true, restartRequired, restartRequired, null);
    }

    /// <inheritdoc />
    public async ValueTask<SettingsApplyResult> ApplyUserSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        var validation = SettingsValidator.Validate(settings);
        if (!validation.IsValid)
        {
            return SettingsApplyResult.Failed(string.Join("; ", validation.Errors.Select(error => $"{error.Property}: {error.Message}")));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var previous = userStore.Load().Settings;
        userStore.Save(settings);
        await history.RecordAsync(new("User", clock(), ChangedUserProperties(previous, settings)), cancellationToken).ConfigureAwait(false);
        return new(true, false, false, null);
    }

    private void RestoreMachineSettings(MachineSettings previous)
    {
        try
        {
            machineStore.Save(previous);
        }
        catch
        {
            // The original apply failure is the actionable result; the host can surface its persistence telemetry.
        }
    }

    private static List<string> ChangedMachineProperties(MachineSettings before, MachineSettings after)
    {
        var changed = new List<string>();
        if (!before.MonitoringPaths.SequenceEqual(after.MonitoringPaths, StringComparer.OrdinalIgnoreCase)) changed.Add(nameof(MachineSettings.MonitoringPaths));
        if (!before.ExcludedPaths.SequenceEqual(after.ExcludedPaths, StringComparer.OrdinalIgnoreCase)) changed.Add(nameof(MachineSettings.ExcludedPaths));
        if (before.NoiseFilter != after.NoiseFilter) changed.Add(nameof(MachineSettings.NoiseFilter));
        if (!string.Equals(before.LogStoragePath, after.LogStoragePath, StringComparison.OrdinalIgnoreCase)) changed.Add(nameof(MachineSettings.LogStoragePath));
        if (!before.MediaMirrors.OrderBy(pair => pair.Key).SequenceEqual(after.MediaMirrors.OrderBy(pair => pair.Key))) changed.Add(nameof(MachineSettings.MediaMirrors));
        if (before.FlushIntervalSeconds != after.FlushIntervalSeconds) changed.Add(nameof(MachineSettings.FlushIntervalSeconds));
        return changed;
    }

    private static List<string> ChangedUserProperties(UserSettings before, UserSettings after)
    {
        var changed = new List<string>();
        if (before.ActivityGroupTimeoutSeconds != after.ActivityGroupTimeoutSeconds) changed.Add(nameof(UserSettings.ActivityGroupTimeoutSeconds));
        if (before.PaneTimeoutSeconds != after.PaneTimeoutSeconds) changed.Add(nameof(UserSettings.PaneTimeoutSeconds));
        if (before.EventStackPageSize != after.EventStackPageSize) changed.Add(nameof(UserSettings.EventStackPageSize));
        if (before.EventStackSort != after.EventStackSort) changed.Add(nameof(UserSettings.EventStackSort));
        if (before.InitialEventStackMode != after.InitialEventStackMode) changed.Add(nameof(UserSettings.InitialEventStackMode));
        if (before.DiffFormat != after.DiffFormat) changed.Add(nameof(UserSettings.DiffFormat));
        if (before.DiffZoomPercent != after.DiffZoomPercent) changed.Add(nameof(UserSettings.DiffZoomPercent));
        if (before.DiffColumns != after.DiffColumns) changed.Add(nameof(UserSettings.DiffColumns));
        if (!before.SavedFilters.SequenceEqual(after.SavedFilters, StringComparer.Ordinal)) changed.Add(nameof(UserSettings.SavedFilters));
        return changed;
    }
}

/// <summary>Authorizer used by hosts that have already enforced the IPC caller identity.</summary>
public sealed class AllowAllAgentSettingsAuthorizer : IAgentSettingsAuthorizer
{
    /// <inheritdoc />
    public bool CanApplyMachineSettings(MachineSettings settings) => true;
}

/// <summary>No-op lifecycle for hosts where monitoring is not running.</summary>
public sealed class NoOpMonitoringLifecycle : IMonitoringLifecycle
{
    /// <inheritdoc />
    public ValueTask RestartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
