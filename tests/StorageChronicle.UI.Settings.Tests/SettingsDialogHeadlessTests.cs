using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using StorageChronicle.Settings;
using StorageChronicle.UI.Settings;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(StorageChronicle.UI.Settings.Tests.SettingsTestApp))]

namespace StorageChronicle.UI.Settings.Tests;

public sealed class SettingsDialogHeadlessTests
{
    [AvaloniaFact]
    public async Task DialogRendersRecoveredAgentValuesAndEditsTheExistingDrafts()
    {
        var gateway = new TestGateway
        {
            Machine = new MachineSettings
            {
                MonitoringPaths = ["C:\\watched"],
                ExcludedPaths = ["C:\\excluded"],
                LogStoragePath = "D:\\chronicle",
                MediaMirrors = new Dictionary<string, string> { ["USB-01"] = "E:\\mirror" },
                FlushIntervalSeconds = 7
            },
            User = new UserSettings { EventStackPageSize = 500, DiffZoomPercent = 125 },
            MachineWarning = "Machine settings were recovered from the previous valid version."
        };
        var dialog = new SettingsDialogWindow(gateway);

        try
        {
            dialog.Show();
            dialog.Measure(new Size(1024, 768));
            dialog.Arrange(new Rect(0, 0, 1024, 768));
            await Task.Yield();

            var monitoringPaths = dialog.FindControl<TextBox>("MonitoringPathsInput");
            var status = dialog.FindControl<TextBlock>("StatusText");

            Assert.NotNull(monitoringPaths);
            Assert.Same(dialog.ViewModel, monitoringPaths!.DataContext);
            Assert.Equal("C:\\watched", dialog.ViewModel.MonitoringPathsText);
            Assert.Equal("C:\\watched", monitoringPaths!.Text);
            Assert.Equal("D:\\chronicle", dialog.ViewModel.LogStoragePathText);
            Assert.Equal("USB-01=E:\\mirror", dialog.ViewModel.MediaMirrorsText);
            Assert.Contains("recovered", status!.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Settings dialog", dialog.GetValue(AutomationProperties.NameProperty));

            monitoringPaths.Text = "C:\\watched\r\nC:\\second";

            Assert.Equal(["C:\\watched", "C:\\second"], dialog.ViewModel.MachineDraft.MonitoringPaths);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public async Task SuccessfulApplyClosesTheModalAndUsesTheAgentGateway()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var gateway = new TestGateway { MachineResult = new(true, true, true, null) };
            var dialog = new SettingsDialogWindow(gateway);

            var dialogTask = dialog.ShowDialogAsync(owner);
            await dialog.ViewModel.ApplyCommand.ExecuteAsync(null);
            var result = await dialogTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(result == true);
            Assert.Equal(1, gateway.MachineApplyCalls);
            Assert.Equal(1, gateway.UserApplyCalls);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task AgentFailureKeepsTheModalOpenAndDoesNotReportSuccess()
    {
        var gateway = new TestGateway { MachineResult = SettingsApplyResult.Failed("permission denied") };
        var dialog = new SettingsDialogWindow(gateway);

        try
        {
            dialog.Show();
            await dialog.ViewModel.ApplyCommand.ExecuteAsync(null);

            Assert.True(dialog.IsVisible);
            Assert.Null(dialog.ViewModel.StatusMessage);
            Assert.Equal("permission denied", dialog.ViewModel.ErrorMessage);
            Assert.Equal(0, gateway.UserApplyCalls);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public async Task CancelButtonClosesWithoutCallingTheAgent()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var gateway = new TestGateway();
            var dialog = new SettingsDialogWindow(gateway);
            var dialogTask = dialog.ShowDialogAsync(owner);

            var cancel = dialog.FindControl<Button>("CancelButton");
            Assert.NotNull(cancel);
            cancel!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var result = await dialogTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(result == true);
            Assert.Equal(0, gateway.MachineApplyCalls);
            Assert.Equal(0, gateway.UserApplyCalls);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task CancelApplyPropagatesCancellationAndLeavesTheDialogUsable()
    {
        var gateway = new TestGateway { BlockMachineApply = true };
        var dialog = new SettingsDialogWindow(gateway);

        try
        {
            dialog.Show();
            var applyTask = dialog.ViewModel.ApplyCommand.ExecuteAsync(null);
            await gateway.MachineApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            dialog.ViewModel.CancelApplyCommand.Execute(null);
            await applyTask;

            Assert.True(dialog.IsVisible);
            Assert.Equal("Settings apply canceled.", dialog.ViewModel.StatusMessage);
            Assert.Null(dialog.ViewModel.ErrorMessage);
            Assert.Equal(0, gateway.UserApplyCalls);
        }
        finally
        {
            dialog.Close();
        }
    }

    private sealed class TestGateway : IAgentSettingsGateway
    {
        public MachineSettings Machine { get; init; } = new()
        {
            MonitoringPaths = ["C:\\watched"],
            LogStoragePath = "C:\\logs"
        };

        public UserSettings User { get; init; } = DefaultSettings.CreateUser();
        public string? MachineWarning { get; init; }
        public SettingsApplyResult MachineResult { get; init; } = new(true, false, false, null);
        public bool BlockMachineApply { get; init; }
        public TaskCompletionSource<bool> MachineApplyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MachineApplyCalls { get; private set; }
        public int UserApplyCalls { get; private set; }

        public SettingsLoadResult<MachineSettings> LoadMachineSettings() => new(Machine, MachineWarning is not null, false, MachineWarning);

        public SettingsLoadResult<UserSettings> LoadUserSettings() => new(User, false, false, null);

        public async ValueTask<SettingsApplyResult> ApplyMachineSettingsAsync(MachineSettings settings, CancellationToken cancellationToken = default)
        {
            MachineApplyCalls++;
            MachineApplyStarted.TrySetResult(true);
            if (BlockMachineApply)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return MachineResult;
        }

        public ValueTask<SettingsApplyResult> ApplyUserSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default)
        {
            UserApplyCalls++;
            return ValueTask.FromResult(new SettingsApplyResult(true, false, false, null));
        }
    }
}

/// <summary>Minimal Avalonia application used by settings Window Headless tests.</summary>
public sealed class SettingsTestApp : Application
{
    /// <summary>Creates the Avalonia headless test platform.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<SettingsTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
