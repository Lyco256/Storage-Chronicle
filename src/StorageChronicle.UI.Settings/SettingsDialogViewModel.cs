using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageChronicle.Settings;

namespace StorageChronicle.UI.Settings;

/// <summary>Headless state for the modal settings dialog.</summary>
public sealed class SettingsDialogViewModel : ObservableObject
{
    private readonly IAgentSettingsGateway gateway;
    private MachineSettings machineDraft;
    private UserSettings userDraft;
    private MachineSettings? loadedMachineSettings;
    private MachineSettings? pendingMachineSettings;
    private UserSettings? pendingUserSettings;
    private SettingsImpactPreview? impactPreview;
    private bool isBusy;
    private string? statusMessage;
    private string? errorMessage;

    /// <summary>Initializes the dialog with an Agent gateway; it receives no file path.</summary>
    public SettingsDialogViewModel(IAgentSettingsGateway gateway)
    {
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        machineDraft = new MachineSettings();
        userDraft = new UserSettings();
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, CanApply);
        ConfirmImpactAndApplyCommand = new AsyncRelayCommand(ConfirmImpactAndApplyAsync, CanConfirmImpactAndApply);
        CancelImpactPreviewCommand = new RelayCommand(CancelImpactPreview, CanCancelImpactPreview);
    }

    /// <summary>Gets or sets the machine settings currently edited by the dialog.</summary>
    public MachineSettings MachineDraft
    {
        get => machineDraft;
        set
        {
            if (SetProperty(ref machineDraft, value))
            {
                ClearImpactPreview();
            }
        }
    }

    /// <summary>Gets or sets the user settings currently edited by the dialog.</summary>
    public UserSettings UserDraft
    {
        get => userDraft;
        set
        {
            if (SetProperty(ref userDraft, value))
            {
                ClearImpactPreview();
            }
        }
    }

    /// <summary>Gets whether an Agent request is in progress.</summary>
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                ApplyCommand.NotifyCanExecuteChanged();
                ConfirmImpactAndApplyCommand.NotifyCanExecuteChanged();
                CancelImpactPreviewCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets a non-error status for the dialog.</summary>
    public string? StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    /// <summary>Gets the last failed apply message, if any.</summary>
    public string? ErrorMessage
    {
        get => errorMessage;
        private set => SetProperty(ref errorMessage, value);
    }

    /// <summary>Gets the impact details that must be reviewed before a machine-settings apply.</summary>
    public SettingsImpactPreview? ImpactPreview
    {
        get => impactPreview;
        private set
        {
            if (SetProperty(ref impactPreview, value))
            {
                OnPropertyChanged(nameof(HasImpactPreview));
                ApplyCommand.NotifyCanExecuteChanged();
                ConfirmImpactAndApplyCommand.NotifyCanExecuteChanged();
                CancelImpactPreviewCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets whether a machine-settings impact preview is awaiting confirmation.</summary>
    public bool HasImpactPreview => ImpactPreview is not null;

    /// <summary>Gets the command bound to the modal Apply button.</summary>
    public IAsyncRelayCommand ApplyCommand { get; }

    /// <summary>Gets the explicit confirmation command for the currently displayed impact preview.</summary>
    public IAsyncRelayCommand ConfirmImpactAndApplyCommand { get; }

    /// <summary>Gets the command that cancels a displayed impact preview without applying settings.</summary>
    public IRelayCommand CancelImpactPreviewCommand { get; }

    /// <summary>Loads both settings scopes through the Agent.</summary>
    public void Load()
    {
        ClearImpactPreview();
        var machine = gateway.LoadMachineSettings();
        var user = gateway.LoadUserSettings();
        loadedMachineSettings = Snapshot(machine.Settings);
        MachineDraft = Snapshot(machine.Settings);
        UserDraft = Snapshot(user.Settings);
        var warnings = new[] { machine.Warning, user.Warning }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        StatusMessage = warnings.Length == 0 ? "Settings loaded." : string.Join(" ", warnings);
        ErrorMessage = null;
    }

    private bool CanApply() => !IsBusy && !HasImpactPreview;

    private bool CanConfirmImpactAndApply() => !IsBusy && HasImpactPreview;

    private bool CanCancelImpactPreview() => !IsBusy && HasImpactPreview;

    private async Task ApplyAsync()
    {
        var machineValidation = SettingsValidator.Validate(MachineDraft);
        var userValidation = SettingsValidator.Validate(UserDraft);
        var validationErrors = machineValidation.Errors.Concat(userValidation.Errors).ToArray();
        if (validationErrors.Length > 0)
        {
            ErrorMessage = string.Join("; ", validationErrors.Select(error => $"{error.Property}: {error.Message}"));
            StatusMessage = null;
            return;
        }

        if (loadedMachineSettings is null)
        {
            ErrorMessage = "Load settings before applying changes so the impact can be reviewed.";
            StatusMessage = null;
            return;
        }

        var machine = Snapshot(MachineDraft);
        var changes = BuildImpactChanges(loadedMachineSettings, machine);
        if (changes.Count > 0)
        {
            pendingMachineSettings = machine;
            pendingUserSettings = Snapshot(UserDraft);
            ImpactPreview = new SettingsImpactPreview(
                Array.AsReadOnly(changes.ToArray()),
                "The Agent may read metadata under the listed monitoring paths and will apply the listed exclusions and collection behavior. " +
                "History/configuration writes are limited to Agent-authorized product-owned settings and durable-history locations. " +
                "For each listed media identifier, mirror append is permitted only after the Agent verifies the actual volume identity, allowed role, and product-owned mirror directory; otherwise it must reject the write. " +
                "Existing source data and unknown/pre-existing same-name media files are not overwritten. Source file contents are not read or stored.");
            ErrorMessage = null;
            StatusMessage = "Review the impact details, then explicitly confirm or cancel. No settings have been applied.";
            return;
        }

        await ApplySettingsAsync(machine, Snapshot(UserDraft)).ConfigureAwait(false);
    }

    private async Task ConfirmImpactAndApplyAsync()
    {
        if (pendingMachineSettings is null || pendingUserSettings is null || ImpactPreview is null)
        {
            return;
        }

        var machine = pendingMachineSettings;
        var user = pendingUserSettings;
        ClearImpactPreview();
        await ApplySettingsAsync(machine, user).ConfigureAwait(false);
    }

    private async Task ApplySettingsAsync(MachineSettings machine, UserSettings user)
    {
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var machineResult = await gateway.ApplyMachineSettingsAsync(machine).ConfigureAwait(false);
            if (!machineResult.Succeeded)
            {
                ErrorMessage = machineResult.Error ?? "Machine settings were rejected by the Agent.";
                return;
            }

            loadedMachineSettings = Snapshot(machine);
            var userResult = await gateway.ApplyUserSettingsAsync(user).ConfigureAwait(false);
            if (!userResult.Succeeded)
            {
                ErrorMessage = userResult.Error ?? "User settings were rejected by the Agent.";
                return;
            }

            StatusMessage = machineResult.RestartRequired ? "Settings applied; monitoring restarted safely." : "Settings applied.";
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Settings could not be applied: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CancelImpactPreview()
    {
        ClearImpactPreview();
        ErrorMessage = null;
        StatusMessage = "Impact review cancelled. No settings were applied.";
    }

    private void ClearImpactPreview()
    {
        pendingMachineSettings = null;
        pendingUserSettings = null;
        ImpactPreview = null;
    }

    private static MachineSettings Snapshot(MachineSettings settings) => settings with
    {
        MonitoringPaths = settings.MonitoringPaths.ToArray(),
        ExcludedPaths = settings.ExcludedPaths.ToArray(),
        MediaMirrors = settings.MediaMirrors.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
    };

    private static UserSettings Snapshot(UserSettings settings) => settings with { SavedFilters = settings.SavedFilters.ToArray() };

    private static List<SettingsImpactChange> BuildImpactChanges(MachineSettings before, MachineSettings after)
    {
        var changes = new List<SettingsImpactChange>();
        AddListChange(changes, "Monitoring paths", before.MonitoringPaths, after.MonitoringPaths,
            "Changes which exact paths the Agent monitors.");
        AddListChange(changes, "Excluded paths", before.ExcludedPaths, after.ExcludedPaths,
            "Changes which exact paths the Agent excludes from monitoring.");

        if (before.NoiseFilter != after.NoiseFilter)
        {
            changes.Add(new("Noise filter", before.NoiseFilter.ToString(), after.NoiseFilter.ToString(), "Changes Agent event filtering behavior."));
        }

        if (!string.Equals(before.LogStoragePath, after.LogStoragePath, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add(new("Log storage path", before.LogStoragePath, after.LogStoragePath, "Changes the product-owned durable history write destination."));
        }

        var mediaIds = before.MediaMirrors.Keys.Concat(after.MediaMirrors.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase);
        foreach (var mediaId in mediaIds)
        {
            before.MediaMirrors.TryGetValue(mediaId, out var oldPath);
            after.MediaMirrors.TryGetValue(mediaId, out var newPath);
            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new($"Media mirror · {mediaId}", oldPath ?? "(disabled)", newPath ?? "(disabled)",
                    $"Changes the history-mirror destination and scope for media '{mediaId}'."));
            }
        }

        if (before.FlushIntervalSeconds != after.FlushIntervalSeconds)
        {
            changes.Add(new("Flush interval", $"{before.FlushIntervalSeconds} seconds", $"{after.FlushIntervalSeconds} seconds", "Changes Agent persistence/flush behavior."));
        }

        return changes;
    }

    private static void AddListChange(List<SettingsImpactChange> changes, string name, IReadOnlyList<string> before, IReadOnlyList<string> after, string impact)
    {
        if (!before.SequenceEqual(after, StringComparer.OrdinalIgnoreCase))
        {
            changes.Add(new(name, FormatPaths(before), FormatPaths(after), impact));
        }
    }

    private static string FormatPaths(IReadOnlyList<string> paths) => paths.Count == 0 ? "(none)" : string.Join(Environment.NewLine, paths);
}

/// <summary>Describes one old-to-new machine-setting change and its operational impact.</summary>
/// <param name="Setting">The setting or media scope being changed.</param>
/// <param name="OldValue">The current value shown to the user.</param>
/// <param name="NewValue">The proposed value shown to the user.</param>
/// <param name="Impact">The monitoring, Agent, or product-write behavior affected.</param>
public sealed record SettingsImpactChange(string Setting, string OldValue, string NewValue, string Impact);

/// <summary>Contains the complete review text required before applying machine-setting changes.</summary>
/// <param name="Changes">The exact old and proposed values for every changed machine setting.</param>
/// <param name="WriteBoundary">The product-owned write boundary and monitored-source read-only statement.</param>
public sealed record SettingsImpactPreview(IReadOnlyList<SettingsImpactChange> Changes, string WriteBoundary);
