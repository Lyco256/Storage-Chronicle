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
    private string monitoringPathsText = string.Empty;
    private string excludedPathsText = string.Empty;
    private NoiseFilterProfile noiseFilter;
    private string logStoragePathText = string.Empty;
    private string mediaMirrorsText = string.Empty;
    private decimal? flushIntervalSeconds;
    private decimal? activityGroupTimeoutSeconds;
    private decimal? paneTimeoutSeconds;
    private decimal? eventStackPageSize;
    private EventStackSortOrder eventStackSort;
    private EventStackInitialMode initialEventStackMode;
    private DiffDisplayFormat diffFormat;
    private decimal? diffZoomPercent;
    private DiffColumnOrder diffColumns;
    private string savedFiltersText = string.Empty;
    private bool isBusy;
    private string? statusMessage;
    private string? errorMessage;
    private string? inputErrorMessage;
    private CancellationTokenSource? applyCancellation;
    private readonly Dictionary<string, string> inputErrors = new(StringComparer.Ordinal);
    private bool synchronizingInputs;

    /// <summary>Initializes the dialog with an Agent gateway; it receives no file path.</summary>
    public SettingsDialogViewModel(IAgentSettingsGateway gateway)
    {
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        machineDraft = new MachineSettings();
        userDraft = new UserSettings();
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, CanApply);
        CancelApplyCommand = new RelayCommand(CancelApply, CanCancelApply);
    }

    /// <summary>Gets or sets the machine settings currently edited by the dialog.</summary>
    public MachineSettings MachineDraft
    {
        get => machineDraft;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref machineDraft, value) && !synchronizingInputs)
            {
                SynchronizeMachineInputs();
            }
        }
    }

    /// <summary>Gets or sets the user settings currently edited by the dialog.</summary>
    public UserSettings UserDraft
    {
        get => userDraft;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref userDraft, value) && !synchronizingInputs)
            {
                SynchronizeUserInputs();
            }
        }
    }

    /// <summary>Gets or sets newline-separated monitoring paths.</summary>
    public string MonitoringPathsText
    {
        get => monitoringPathsText;
        set
        {
            value ??= string.Empty;
            if (SetProperty(ref monitoringPathsText, value))
            {
                MachineDraft = MachineDraft with { MonitoringPaths = ParseLines(value) };
            }
        }
    }

    /// <summary>Gets or sets newline-separated excluded paths.</summary>
    public string ExcludedPathsText
    {
        get => excludedPathsText;
        set
        {
            value ??= string.Empty;
            if (SetProperty(ref excludedPathsText, value))
            {
                MachineDraft = MachineDraft with { ExcludedPaths = ParseLines(value) };
            }
        }
    }

    /// <summary>Gets or sets the machine noise-filter profile.</summary>
    public NoiseFilterProfile NoiseFilter
    {
        get => noiseFilter;
        set
        {
            if (SetProperty(ref noiseFilter, value))
            {
                MachineDraft = MachineDraft with { NoiseFilter = value };
            }
        }
    }

    /// <summary>Gets or sets the durable log storage path.</summary>
    public string LogStoragePathText
    {
        get => logStoragePathText;
        set
        {
            value ??= string.Empty;
            if (SetProperty(ref logStoragePathText, value))
            {
                MachineDraft = MachineDraft with { LogStoragePath = value };
            }
        }
    }

    /// <summary>Gets or sets media mirror entries in <c>media-id=absolute-path</c> form.</summary>
    public string MediaMirrorsText
    {
        get => mediaMirrorsText;
        set
        {
            value ??= string.Empty;
            if (!SetProperty(ref mediaMirrorsText, value))
            {
                return;
            }

            if (TryParseMediaMirrors(value, out var mirrors, out var error))
            {
                SetInputError(nameof(MediaMirrorsText), null);
                MachineDraft = MachineDraft with { MediaMirrors = mirrors };
            }
            else
            {
                SetInputError(nameof(MediaMirrorsText), error);
            }
        }
    }

    /// <summary>Gets or sets the machine flush interval in seconds.</summary>
    public decimal? FlushIntervalSeconds
    {
        get => flushIntervalSeconds;
        set
        {
            if (!SetProperty(ref flushIntervalSeconds, value))
            {
                return;
            }

            if (value is { } seconds && decimal.Truncate(seconds) == seconds)
            {
                SetInputError(nameof(FlushIntervalSeconds), null);
                MachineDraft = MachineDraft with { FlushIntervalSeconds = decimal.ToInt32(seconds) };
            }
            else
            {
                SetInputError(nameof(FlushIntervalSeconds), "Flush interval must be a whole number of seconds.");
            }
        }
    }

    /// <summary>Gets or sets the Activity Group timeout in seconds.</summary>
    public decimal? ActivityGroupTimeoutSeconds
    {
        get => activityGroupTimeoutSeconds;
        set
        {
            if (!SetProperty(ref activityGroupTimeoutSeconds, value))
            {
                return;
            }

            if (value is { } seconds)
            {
                SetInputError(nameof(ActivityGroupTimeoutSeconds), null);
                UserDraft = UserDraft with { ActivityGroupTimeoutSeconds = (double)seconds };
            }
            else
            {
                SetInputError(nameof(ActivityGroupTimeoutSeconds), "Activity Group timeout is required.");
            }
        }
    }

    /// <summary>Gets or sets the Diff View pane timeout in seconds.</summary>
    public decimal? PaneTimeoutSeconds
    {
        get => paneTimeoutSeconds;
        set
        {
            if (!SetProperty(ref paneTimeoutSeconds, value))
            {
                return;
            }

            if (value is { } seconds)
            {
                SetInputError(nameof(PaneTimeoutSeconds), null);
                UserDraft = UserDraft with { PaneTimeoutSeconds = (double)seconds };
            }
            else
            {
                SetInputError(nameof(PaneTimeoutSeconds), "Pane timeout is required.");
            }
        }
    }

    /// <summary>Gets or sets the number of rows in one Event Stack page.</summary>
    public decimal? EventStackPageSize
    {
        get => eventStackPageSize;
        set
        {
            if (!SetProperty(ref eventStackPageSize, value))
            {
                return;
            }

            if (value is { } pageSize && decimal.Truncate(pageSize) == pageSize)
            {
                SetInputError(nameof(EventStackPageSize), null);
                UserDraft = UserDraft with { EventStackPageSize = decimal.ToInt32(pageSize) };
            }
            else
            {
                SetInputError(nameof(EventStackPageSize), "Event Stack page size must be a whole number of rows.");
            }
        }
    }

    /// <summary>Gets or sets the Event Stack row ordering.</summary>
    public EventStackSortOrder EventStackSort
    {
        get => eventStackSort;
        set
        {
            if (SetProperty(ref eventStackSort, value))
            {
                UserDraft = UserDraft with { EventStackSort = value };
            }
        }
    }

    /// <summary>Gets or sets the initial Event Stack mode.</summary>
    public EventStackInitialMode InitialEventStackMode
    {
        get => initialEventStackMode;
        set
        {
            if (SetProperty(ref initialEventStackMode, value))
            {
                UserDraft = UserDraft with { InitialEventStackMode = value };
            }
        }
    }

    /// <summary>Gets or sets the Diff View display format.</summary>
    public DiffDisplayFormat DiffFormat
    {
        get => diffFormat;
        set
        {
            if (SetProperty(ref diffFormat, value))
            {
                UserDraft = UserDraft with { DiffFormat = value };
            }
        }
    }

    /// <summary>Gets or sets the Diff View zoom percentage.</summary>
    public decimal? DiffZoomPercent
    {
        get => diffZoomPercent;
        set
        {
            if (!SetProperty(ref diffZoomPercent, value))
            {
                return;
            }

            if (value is { } zoom && decimal.Truncate(zoom) == zoom)
            {
                SetInputError(nameof(DiffZoomPercent), null);
                UserDraft = UserDraft with { DiffZoomPercent = decimal.ToInt32(zoom) };
            }
            else
            {
                SetInputError(nameof(DiffZoomPercent), "Diff View zoom must be a whole percentage.");
            }
        }
    }

    /// <summary>Gets or sets the Diff View column ordering.</summary>
    public DiffColumnOrder DiffColumns
    {
        get => diffColumns;
        set
        {
            if (SetProperty(ref diffColumns, value))
            {
                UserDraft = UserDraft with { DiffColumns = value };
            }
        }
    }

    /// <summary>Gets or sets newline-separated saved filters.</summary>
    public string SavedFiltersText
    {
        get => savedFiltersText;
        set
        {
            value ??= string.Empty;
            if (SetProperty(ref savedFiltersText, value))
            {
                UserDraft = UserDraft with { SavedFilters = ParseLines(value) };
            }
        }
    }

    /// <summary>Gets the available machine noise-filter profiles.</summary>
    public IReadOnlyList<NoiseFilterProfile> NoiseFilterProfiles { get; } = Enum.GetValues<NoiseFilterProfile>();

    /// <summary>Gets the available Event Stack sort orders.</summary>
    public IReadOnlyList<EventStackSortOrder> EventStackSortOrders { get; } = Enum.GetValues<EventStackSortOrder>();

    /// <summary>Gets the available initial Event Stack modes.</summary>
    public IReadOnlyList<EventStackInitialMode> EventStackInitialModes { get; } = Enum.GetValues<EventStackInitialMode>();

    /// <summary>Gets the available Diff View display formats.</summary>
    public IReadOnlyList<DiffDisplayFormat> DiffDisplayFormats { get; } = Enum.GetValues<DiffDisplayFormat>();

    /// <summary>Gets the available Diff View column orders.</summary>
    public IReadOnlyList<DiffColumnOrder> DiffColumnOrders { get; } = Enum.GetValues<DiffColumnOrder>();

    /// <summary>Gets whether an Agent request is in progress.</summary>
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                ApplyCommand.NotifyCanExecuteChanged();
                CancelApplyCommand.NotifyCanExecuteChanged();
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

    /// <summary>Gets an input-format error raised before settings validation.</summary>
    public string? InputErrorMessage
    {
        get => inputErrorMessage;
        private set => SetProperty(ref inputErrorMessage, value);
    }

    /// <summary>Gets the command bound to the modal Apply button.</summary>
    public IAsyncRelayCommand ApplyCommand { get; }

    /// <summary>Gets the command that cancels an in-flight Agent apply request.</summary>
    public IRelayCommand CancelApplyCommand { get; }

    /// <summary>Raised after both machine and user settings have been applied successfully.</summary>
    public event EventHandler? ApplySucceeded;

    /// <summary>Loads both settings scopes through the Agent.</summary>
    public void Load()
    {
        inputErrors.Clear();
        InputErrorMessage = null;
        var machine = gateway.LoadMachineSettings();
        var user = gateway.LoadUserSettings();
        MachineDraft = machine.Settings;
        UserDraft = user.Settings;
        var warnings = new[] { machine.Warning, user.Warning }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        StatusMessage = warnings.Length == 0 ? "Settings loaded." : string.Join(" ", warnings);
        ErrorMessage = null;
    }

    private bool CanApply() => !IsBusy;

    private bool CanCancelApply() => IsBusy;

    private void CancelApply() => applyCancellation?.Cancel();

    private async Task ApplyAsync()
    {
        if (!string.IsNullOrWhiteSpace(InputErrorMessage))
        {
            ErrorMessage = InputErrorMessage;
            StatusMessage = null;
            return;
        }

        var machineValidation = SettingsValidator.Validate(MachineDraft);
        var userValidation = SettingsValidator.Validate(UserDraft);
        var validationErrors = machineValidation.Errors.Concat(userValidation.Errors).ToArray();
        if (validationErrors.Length > 0)
        {
            ErrorMessage = string.Join("; ", validationErrors.Select(error => $"{error.Property}: {error.Message}"));
            StatusMessage = null;
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        using var cancellation = new CancellationTokenSource();
        applyCancellation = cancellation;
        try
        {
            var machineResult = await gateway.ApplyMachineSettingsAsync(MachineDraft, cancellation.Token);
            if (!machineResult.Succeeded)
            {
                ErrorMessage = machineResult.Error ?? "Machine settings were rejected by the Agent.";
                return;
            }

            var userResult = await gateway.ApplyUserSettingsAsync(UserDraft, cancellation.Token);
            if (!userResult.Succeeded)
            {
                ErrorMessage = userResult.Error ?? "User settings were rejected by the Agent.";
                return;
            }

            StatusMessage = machineResult.RestartRequired
                ? machineResult.RestartCompleted
                    ? "Settings applied; monitoring restarted safely."
                    : "Settings applied; monitoring restart is pending."
                : "Settings applied.";
            ApplySucceeded?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = null;
            StatusMessage = "Settings apply canceled.";
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Settings could not be applied: {exception.Message}";
        }
        finally
        {
            applyCancellation = null;
            IsBusy = false;
        }
    }

    private void SynchronizeMachineInputs()
    {
        synchronizingInputs = true;
        try
        {
            SetProperty(ref monitoringPathsText, string.Join(Environment.NewLine, MachineDraft.MonitoringPaths));
            SetProperty(ref excludedPathsText, string.Join(Environment.NewLine, MachineDraft.ExcludedPaths));
            SetProperty(ref noiseFilter, MachineDraft.NoiseFilter);
            SetProperty(ref logStoragePathText, MachineDraft.LogStoragePath);
            SetProperty(ref mediaMirrorsText, FormatMediaMirrors(MachineDraft.MediaMirrors));
            SetProperty(ref flushIntervalSeconds, MachineDraft.FlushIntervalSeconds);
        }
        finally
        {
            synchronizingInputs = false;
        }
    }

    private void SynchronizeUserInputs()
    {
        synchronizingInputs = true;
        try
        {
            SetProperty(ref activityGroupTimeoutSeconds, (decimal)UserDraft.ActivityGroupTimeoutSeconds);
            SetProperty(ref paneTimeoutSeconds, (decimal)UserDraft.PaneTimeoutSeconds);
            SetProperty(ref eventStackPageSize, UserDraft.EventStackPageSize);
            SetProperty(ref eventStackSort, UserDraft.EventStackSort);
            SetProperty(ref initialEventStackMode, UserDraft.InitialEventStackMode);
            SetProperty(ref diffFormat, UserDraft.DiffFormat);
            SetProperty(ref diffZoomPercent, UserDraft.DiffZoomPercent);
            SetProperty(ref diffColumns, UserDraft.DiffColumns);
            SetProperty(ref savedFiltersText, string.Join(Environment.NewLine, UserDraft.SavedFilters));
        }
        finally
        {
            synchronizingInputs = false;
        }
    }

    private void SetInputError(string key, string? message)
    {
        if (message is null)
        {
            inputErrors.Remove(key);
        }
        else
        {
            inputErrors[key] = message;
        }

        InputErrorMessage = inputErrors.Count == 0 ? null : string.Join(" ", inputErrors.Values);
    }

    private static IReadOnlyList<string> ParseLines(string text) => text
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string FormatMediaMirrors(IReadOnlyDictionary<string, string> mirrors) => string.Join(
        Environment.NewLine,
        mirrors.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}={pair.Value}"));

    private static bool TryParseMediaMirrors(
        string text,
        out IReadOnlyDictionary<string, string> mirrors,
        out string? error)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
            {
                mirrors = parsed;
                error = "Each media mirror must use media-id=absolute-path format.";
                return false;
            }

            var mediaId = line[..separator].Trim();
            var path = line[(separator + 1)..].Trim();
            if (!parsed.TryAdd(mediaId, path))
            {
                mirrors = parsed;
                error = $"Media mirror '{mediaId}' is listed more than once.";
                return false;
            }
        }

        mirrors = parsed;
        error = null;
        return true;
    }
}
