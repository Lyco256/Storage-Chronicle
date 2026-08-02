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
    }

    /// <summary>Gets or sets the machine settings currently edited by the dialog.</summary>
    public MachineSettings MachineDraft
    {
        get => machineDraft;
        set => SetProperty(ref machineDraft, value);
    }

    /// <summary>Gets or sets the user settings currently edited by the dialog.</summary>
    public UserSettings UserDraft
    {
        get => userDraft;
        set => SetProperty(ref userDraft, value);
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

    /// <summary>Gets the command bound to the modal Apply button.</summary>
    public IAsyncRelayCommand ApplyCommand { get; }

    /// <summary>Loads both settings scopes through the Agent.</summary>
    public void Load()
    {
        var machine = gateway.LoadMachineSettings();
        var user = gateway.LoadUserSettings();
        MachineDraft = machine.Settings;
        UserDraft = user.Settings;
        var warnings = new[] { machine.Warning, user.Warning }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        StatusMessage = warnings.Length == 0 ? "Settings loaded." : string.Join(" ", warnings);
        ErrorMessage = null;
    }

    private bool CanApply() => !IsBusy;

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

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var machineResult = await gateway.ApplyMachineSettingsAsync(MachineDraft).ConfigureAwait(false);
            if (!machineResult.Succeeded)
            {
                ErrorMessage = machineResult.Error ?? "Machine settings were rejected by the Agent.";
                return;
            }

            var userResult = await gateway.ApplyUserSettingsAsync(UserDraft).ConfigureAwait(false);
            if (!userResult.Succeeded)
            {
                ErrorMessage = userResult.Error ?? "User settings were rejected by the Agent.";
                return;
            }

            StatusMessage = machineResult.RestartRequired ? "Settings applied; monitoring restarted safely." : "Settings applied.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
