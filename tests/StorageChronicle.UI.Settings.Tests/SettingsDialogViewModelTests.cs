using StorageChronicle.Settings;
using StorageChronicle.UI.Settings;
using Xunit;

namespace StorageChronicle.UI.Settings.Tests;

public sealed class SettingsDialogViewModelTests
{
    [Fact]
    public async Task LoadUsesAgentValuesAndSurfacesRecoveryWarning()
    {
        var gateway = new FakeGateway { MachineWarning = "machine recovered" };
        var viewModel = new SettingsDialogViewModel(gateway);

        await viewModel.LoadAsync();

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
        await viewModel.LoadAsync();

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
        await viewModel.LoadAsync();

        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("Settings applied; monitoring restarted safely.", viewModel.StatusMessage);
        Assert.Null(viewModel.ErrorMessage);
    }

    private sealed class FakeGateway : IAgentSettingsGateway
    {
        public MachineSettings Machine { get; } = new() { MonitoringPaths = new[] { "C:\\watched" }, LogStoragePath = "C:\\logs" };
        public UserSettings User { get; } = DefaultSettings.CreateUser();
        public string? MachineWarning { get; init; }
        public SettingsApplyResult MachineResult { get; init; } = new(true, false, false, null);
        public int ApplyCalls { get; private set; }
        public ValueTask<SettingsLoadResult<MachineSettings>> LoadMachineSettingsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SettingsLoadResult<MachineSettings>(Machine, MachineWarning is not null, false, MachineWarning));
        public ValueTask<SettingsLoadResult<UserSettings>> LoadUserSettingsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SettingsLoadResult<UserSettings>(User, false, false, null));
        public ValueTask<SettingsApplyResult> ApplyMachineSettingsAsync(MachineSettings settings, CancellationToken cancellationToken = default)
        {
            ApplyCalls++;
            return ValueTask.FromResult(MachineResult);
        }
        public ValueTask<SettingsApplyResult> ApplyUserSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default) => ValueTask.FromResult(new SettingsApplyResult(true, false, false, null));
    }
}
