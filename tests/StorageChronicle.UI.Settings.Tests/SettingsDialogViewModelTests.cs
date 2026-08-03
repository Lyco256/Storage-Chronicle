using StorageChronicle.Settings;
using StorageChronicle.UI.Settings;
using Xunit;

namespace StorageChronicle.UI.Settings.Tests;

public sealed class SettingsDialogViewModelTests
{
    [Fact]
    public void LoadUsesAgentValuesAndSurfacesRecoveryWarning()
    {
        var gateway = new FakeGateway { MachineWarning = "machine recovered" };
        var viewModel = new SettingsDialogViewModel(gateway);

        viewModel.Load();

        Assert.Equal(gateway.Machine, viewModel.MachineDraft);
        Assert.Equal(gateway.User, viewModel.UserDraft);
        Assert.Contains("machine recovered", viewModel.StatusMessage);
    }

    [Fact]
    public async Task InvalidDraftDoesNotCallAgentOrReportSuccess()
    {
        var gateway = new FakeGateway();
        var viewModel = new SettingsDialogViewModel(gateway)
        {
            MachineDraft = gateway.Machine with { FlushIntervalSeconds = 61 }
        };

        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(0, gateway.ApplyCalls);
        Assert.Null(viewModel.StatusMessage);
        Assert.Contains("FlushIntervalSeconds", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task AgentFailureIsNotShownAsSuccess()
    {
        var gateway = new FakeGateway { MachineResult = SettingsApplyResult.Failed("permission denied") };
        var viewModel = new SettingsDialogViewModel(gateway);
        viewModel.Load();

        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, gateway.ApplyCalls);
        Assert.Null(viewModel.StatusMessage);
        Assert.Equal("permission denied", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task SuccessfulApplyShowsRestartState()
    {
        var gateway = new FakeGateway { MachineResult = new(true, true, true, null) };
        var viewModel = new SettingsDialogViewModel(gateway);
        viewModel.Load();

        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("Settings applied; monitoring restarted safely.", viewModel.StatusMessage);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public void EditorPropertiesSynchronizeMachineAndUserDrafts()
    {
        var gateway = new FakeGateway();
        var viewModel = new SettingsDialogViewModel(gateway);

        viewModel.MonitoringPathsText = "C:\\one\r\n C:\\two ";
        viewModel.ExcludedPathsText = "C:\\excluded";
        viewModel.NoiseFilter = NoiseFilterProfile.Aggressive;
        viewModel.LogStoragePathText = "D:\\chronicle";
        viewModel.MediaMirrorsText = "USB-02=F:\\mirror\r\nUSB-01=E:\\mirror";
        viewModel.FlushIntervalSeconds = 10;
        viewModel.ActivityGroupTimeoutSeconds = 3.5m;
        viewModel.PaneTimeoutSeconds = 8m;
        viewModel.EventStackPageSize = 500m;
        viewModel.EventStackSort = EventStackSortOrder.OldestFirst;
        viewModel.InitialEventStackMode = EventStackInitialMode.ActivityGroup;
        viewModel.DiffFormat = DiffDisplayFormat.Unified;
        viewModel.DiffZoomPercent = 125m;
        viewModel.DiffColumns = DiffColumnOrder.AfterBefore;
        viewModel.SavedFiltersText = "kind:file\r\nname:report";

        Assert.Equal(["C:\\one", "C:\\two"], viewModel.MachineDraft.MonitoringPaths);
        Assert.Equal(["C:\\excluded"], viewModel.MachineDraft.ExcludedPaths);
        Assert.Equal(NoiseFilterProfile.Aggressive, viewModel.MachineDraft.NoiseFilter);
        Assert.Equal("D:\\chronicle", viewModel.MachineDraft.LogStoragePath);
        Assert.Equal("E:\\mirror", viewModel.MachineDraft.MediaMirrors["USB-01"]);
        Assert.Equal(10, viewModel.MachineDraft.FlushIntervalSeconds);
        Assert.Equal(3.5, viewModel.UserDraft.ActivityGroupTimeoutSeconds);
        Assert.Equal(EventStackInitialMode.ActivityGroup, viewModel.UserDraft.InitialEventStackMode);
        Assert.Equal(["kind:file", "name:report"], viewModel.UserDraft.SavedFilters);
        Assert.Null(viewModel.InputErrorMessage);
    }

    [Fact]
    public void NumericAndMediaInputErrorsClearAfterCorrectedInput()
    {
        var viewModel = new SettingsDialogViewModel(new FakeGateway());

        viewModel.FlushIntervalSeconds = 1.5m;
        Assert.Contains("whole number", viewModel.InputErrorMessage);
        viewModel.FlushIntervalSeconds = 5m;
        Assert.Null(viewModel.InputErrorMessage);

        viewModel.ActivityGroupTimeoutSeconds = null;
        viewModel.PaneTimeoutSeconds = null;
        viewModel.EventStackPageSize = 1.5m;
        viewModel.DiffZoomPercent = 100.5m;
        Assert.NotNull(viewModel.InputErrorMessage);
        viewModel.ActivityGroupTimeoutSeconds = 2m;
        viewModel.PaneTimeoutSeconds = 5m;
        viewModel.EventStackPageSize = 250m;
        viewModel.DiffZoomPercent = 100m;
        Assert.Null(viewModel.InputErrorMessage);

        viewModel.MediaMirrorsText = "USB-01=C:\\one\r\nUSB-01=C:\\two";
        Assert.Contains("more than once", viewModel.InputErrorMessage);
        viewModel.MediaMirrorsText = string.Empty;
        Assert.Null(viewModel.InputErrorMessage);
    }

    [Fact]
    public async Task ApplyReportsFallbackMessagesAndUnexpectedExceptions()
    {
        var failedGateway = new FakeGateway { MachineResult = new(false, false, false, null) };
        var failedViewModel = new SettingsDialogViewModel(failedGateway);
        failedViewModel.Load();
        await failedViewModel.ApplyCommand.ExecuteAsync(null);
        Assert.Equal("Machine settings were rejected by the Agent.", failedViewModel.ErrorMessage);

        var userFailureGateway = new FakeGateway { UserResult = new(false, false, false, null) };
        var userFailureViewModel = new SettingsDialogViewModel(userFailureGateway);
        userFailureViewModel.Load();
        await userFailureViewModel.ApplyCommand.ExecuteAsync(null);
        Assert.Equal("User settings were rejected by the Agent.", userFailureViewModel.ErrorMessage);

        var exceptionGateway = new FakeGateway { MachineException = new InvalidOperationException("pipe closed") };
        var exceptionViewModel = new SettingsDialogViewModel(exceptionGateway);
        exceptionViewModel.Load();
        await exceptionViewModel.ApplyCommand.ExecuteAsync(null);
        Assert.Contains("pipe closed", exceptionViewModel.ErrorMessage);
    }

    [Fact]
    public async Task ApplyCancellationIsVisibleAndDoesNotApplyUserSettings()
    {
        var gateway = new FakeGateway { CancelMachine = true };
        var viewModel = new SettingsDialogViewModel(gateway);
        viewModel.Load();

        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("Settings apply canceled.", viewModel.StatusMessage);
        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal(0, gateway.UserApplyCalls);
    }

    private sealed class FakeGateway : IAgentSettingsGateway
    {
        public MachineSettings Machine { get; } = new() { MonitoringPaths = new[] { "C:\\watched" }, LogStoragePath = "C:\\logs" };
        public UserSettings User { get; } = DefaultSettings.CreateUser();
        public string? MachineWarning { get; init; }
        public SettingsApplyResult MachineResult { get; init; } = new(true, false, false, null);
        public SettingsApplyResult UserResult { get; init; } = new(true, false, false, null);
        public Exception? MachineException { get; init; }
        public bool CancelMachine { get; init; }
        public int ApplyCalls { get; private set; }
        public int UserApplyCalls { get; private set; }
        public SettingsLoadResult<MachineSettings> LoadMachineSettings() => new(Machine, MachineWarning is not null, false, MachineWarning);
        public SettingsLoadResult<UserSettings> LoadUserSettings() => new(User, false, false, null);
        public ValueTask<SettingsApplyResult> ApplyMachineSettingsAsync(MachineSettings settings, CancellationToken cancellationToken = default)
        {
            ApplyCalls++;
            if (MachineException is not null) throw MachineException;
            if (CancelMachine) throw new OperationCanceledException(cancellationToken);
            return ValueTask.FromResult(MachineResult);
        }
        public ValueTask<SettingsApplyResult> ApplyUserSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default)
        {
            UserApplyCalls++;
            return ValueTask.FromResult(UserResult);
        }
    }
}
