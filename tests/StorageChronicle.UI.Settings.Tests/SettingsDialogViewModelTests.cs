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

        Assert.Equal(gateway.Machine.MonitoringPaths, viewModel.MachineDraft.MonitoringPaths);
        Assert.Equal(gateway.Machine.ExcludedPaths, viewModel.MachineDraft.ExcludedPaths);
        Assert.Equal(gateway.Machine.LogStoragePath, viewModel.MachineDraft.LogStoragePath);
        Assert.Equal(gateway.User.EventStackPageSize, viewModel.UserDraft.EventStackPageSize);
        Assert.Equal(gateway.User.SavedFilters, viewModel.UserDraft.SavedFilters);
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
    public async Task MachineImpactIsShownBeforeGatewayAndConfirmAppliesTheReviewedSnapshot()
    {
        var gateway = new FakeGateway();
        var viewModel = new SettingsDialogViewModel(gateway);
        viewModel.Load();
        var proposed = gateway.Machine with
        {
            MonitoringPaths = new[] { "C:\\watched", "D:\\archive" },
            MediaMirrors = new Dictionary<string, string> { ["USB-42"] = "E:\\StorageChronicle\\History" },
            FlushIntervalSeconds = 10
        };
        viewModel.MachineDraft = proposed;

        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(0, gateway.ApplyCalls);
        Assert.True(viewModel.HasImpactPreview);
        Assert.False(viewModel.ApplyCommand.CanExecute(null));
        Assert.Contains(viewModel.ImpactPreview!.Changes, change => change.Setting == "Monitoring paths" && change.OldValue == "C:\\watched" && change.NewValue.Contains("D:\\archive", StringComparison.Ordinal));
        Assert.Contains(viewModel.ImpactPreview.Changes, change => change.Setting == "Media mirror · USB-42" && change.NewValue == "E:\\StorageChronicle\\History");
        Assert.Contains(viewModel.ImpactPreview.Changes, change => change.Setting == "Flush interval" && change.NewValue == "10 seconds");
        Assert.Contains("product-owned", viewModel.ImpactPreview.WriteBoundary, StringComparison.Ordinal);
        Assert.Contains("otherwise it must reject", viewModel.ImpactPreview.WriteBoundary, StringComparison.Ordinal);
        Assert.IsNotType<List<SettingsImpactChange>>(viewModel.ImpactPreview.Changes);

        viewModel.MachineDraft = proposed with { MonitoringPaths = new[] { "C:\\watched", "D:\\changed-after-preview" } };
        Assert.False(viewModel.HasImpactPreview);
        Assert.Equal(0, gateway.ApplyCalls);

        await viewModel.ApplyCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasImpactPreview);
        await viewModel.ConfirmImpactAndApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, gateway.ApplyCalls);
        Assert.Equal("D:\\changed-after-preview", Assert.Single(gateway.AppliedMachineSettings!.MonitoringPaths.Skip(1)));
        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal("Settings applied.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task CancellingImpactPreviewNeverInvokesGateway()
    {
        var gateway = new FakeGateway();
        var viewModel = new SettingsDialogViewModel(gateway);
        viewModel.Load();
        viewModel.MachineDraft = gateway.Machine with { ExcludedPaths = new[] { "C:\\private" } };

        await viewModel.ApplyCommand.ExecuteAsync(null);
        viewModel.CancelImpactPreviewCommand.Execute(null);

        Assert.False(viewModel.HasImpactPreview);
        Assert.Equal(0, gateway.ApplyCalls);
        Assert.Equal("Impact review cancelled. No settings were applied.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task UserSettingsOnlyUseOrdinaryApplyPathWithoutImpactPreview()
    {
        var gateway = new FakeGateway();
        var viewModel = new SettingsDialogViewModel(gateway);
        viewModel.Load();
        viewModel.UserDraft = gateway.User with { EventStackPageSize = 500 };

        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasImpactPreview);
        Assert.Equal(1, gateway.ApplyCalls);
        Assert.Equal(500, gateway.AppliedUserSettings!.EventStackPageSize);
        Assert.Equal("Settings applied.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task FailureAfterExplicitConfirmationIsNotReportedAsSuccess()
    {
        var gateway = new FakeGateway { MachineResult = SettingsApplyResult.Failed("restart denied") };
        var viewModel = new SettingsDialogViewModel(gateway);
        viewModel.Load();
        viewModel.MachineDraft = gateway.Machine with { NoiseFilter = NoiseFilterProfile.Aggressive };

        await viewModel.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(0, gateway.ApplyCalls);
        await viewModel.ConfirmImpactAndApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, gateway.ApplyCalls);
        Assert.Null(viewModel.StatusMessage);
        Assert.Equal("restart denied", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task GatewayExceptionAfterExplicitConfirmationIsNotReportedAsSuccess()
    {
        var gateway = new FakeGateway { ThrowOnMachineApply = true };
        var viewModel = new SettingsDialogViewModel(gateway);
        viewModel.Load();
        viewModel.MachineDraft = gateway.Machine with { LogStoragePath = "D:\\ChronicleHistory" };

        await viewModel.ApplyCommand.ExecuteAsync(null);
        await viewModel.ConfirmImpactAndApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, gateway.ApplyCalls);
        Assert.Null(viewModel.StatusMessage);
        Assert.Contains("storage unavailable", viewModel.ErrorMessage);
    }

    private sealed class FakeGateway : IAgentSettingsGateway
    {
        public MachineSettings Machine { get; init; } = new() { MonitoringPaths = new[] { "C:\\watched" }, LogStoragePath = "C:\\logs" };
        public UserSettings User { get; } = DefaultSettings.CreateUser();
        public string? MachineWarning { get; init; }
        public SettingsApplyResult MachineResult { get; init; } = new(true, false, false, null);
        public bool ThrowOnMachineApply { get; init; }
        public int ApplyCalls { get; private set; }
        public MachineSettings? AppliedMachineSettings { get; private set; }
        public UserSettings? AppliedUserSettings { get; private set; }
        public SettingsLoadResult<MachineSettings> LoadMachineSettings() => new(Machine, MachineWarning is not null, false, MachineWarning);
        public SettingsLoadResult<UserSettings> LoadUserSettings() => new(User, false, false, null);
        public ValueTask<SettingsApplyResult> ApplyMachineSettingsAsync(MachineSettings settings, CancellationToken cancellationToken = default)
        {
            ApplyCalls++;
            AppliedMachineSettings = settings;
            if (ThrowOnMachineApply)
            {
                throw new IOException("storage unavailable");
            }

            return ValueTask.FromResult(MachineResult);
        }
        public ValueTask<SettingsApplyResult> ApplyUserSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default)
        {
            AppliedUserSettings = settings;
            return ValueTask.FromResult(new SettingsApplyResult(true, false, false, null));
        }
    }
}
