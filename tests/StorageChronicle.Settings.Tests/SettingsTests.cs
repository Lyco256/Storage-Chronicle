using System.Text;
using StorageChronicle.Settings;
using Xunit;

namespace StorageChronicle.Settings.Tests;

public sealed class SettingsTests
{
    [Fact]
    public void DefaultsUseRequiredValues()
    {
        var user = DefaultSettings.CreateUser();
        Assert.Equal(2, user.ActivityGroupTimeoutSeconds);
        Assert.Equal(5, user.PaneTimeoutSeconds);
        Assert.Equal(250, user.EventStackPageSize);
        Assert.Equal(100, user.DiffZoomPercent);
        Assert.Equal(5, new MachineSettings().FlushIntervalSeconds);
    }

    [Fact]
    public void MachineSettingsRoundTripIncludesEveryField()
    {
        using var fixture = new SettingsFixture();
        var expected = new MachineSettings
        {
            MonitoringPaths = new[] { fixture.Root, fixture.Root + "\\watched" },
            ExcludedPaths = new[] { fixture.Root + "\\excluded" },
            NoiseFilter = NoiseFilterProfile.Aggressive,
            LogStoragePath = fixture.Root + "\\logs",
            MediaMirrors = new Dictionary<string, string> { ["usb-a"] = fixture.Root + "\\mirror" },
            FlushIntervalSeconds = 60
        };

        fixture.Machine.Save(expected);
        var actual = fixture.Machine.Load().Settings;

        Assert.Equal(expected.NoiseFilter, actual.NoiseFilter);
        Assert.Equal(expected.LogStoragePath, actual.LogStoragePath);
        Assert.Equal(expected.FlushIntervalSeconds, actual.FlushIntervalSeconds);
        Assert.Equal(expected.MonitoringPaths, actual.MonitoringPaths);
        Assert.Equal(expected.ExcludedPaths, actual.ExcludedPaths);
        Assert.Equal(expected.MediaMirrors, actual.MediaMirrors);
        var bytes = fixture.ReadBytes(fixture.MachinePath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    [Fact]
    public void UserSettingsRoundTripIncludesEveryField()
    {
        using var fixture = new SettingsFixture();
        var expected = new UserSettings
        {
            ActivityGroupTimeoutSeconds = 0.5,
            PaneTimeoutSeconds = 60,
            EventStackPageSize = 5000,
            EventStackSort = EventStackSortOrder.OldestFirst,
            InitialEventStackMode = EventStackInitialMode.File,
            DiffFormat = DiffDisplayFormat.Unified,
            DiffZoomPercent = 300,
            DiffColumns = DiffColumnOrder.AfterBefore,
            SavedFilters = new[] { "kind:file", "quality:exact" }
        };

        fixture.User.Save(expected);

        var actual = fixture.User.Load().Settings;
        Assert.Equal(expected.ActivityGroupTimeoutSeconds, actual.ActivityGroupTimeoutSeconds);
        Assert.Equal(expected.PaneTimeoutSeconds, actual.PaneTimeoutSeconds);
        Assert.Equal(expected.EventStackPageSize, actual.EventStackPageSize);
        Assert.Equal(expected.EventStackSort, actual.EventStackSort);
        Assert.Equal(expected.InitialEventStackMode, actual.InitialEventStackMode);
        Assert.Equal(expected.DiffFormat, actual.DiffFormat);
        Assert.Equal(expected.DiffZoomPercent, actual.DiffZoomPercent);
        Assert.Equal(expected.DiffColumns, actual.DiffColumns);
        Assert.Equal(expected.SavedFilters, actual.SavedFilters);
    }

    [Fact]
    public void LegacySchemaAndUnknownFieldsAreAccepted()
    {
        using var fixture = new SettingsFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.MachinePath)!);
        File.WriteAllText(fixture.MachinePath, "{\"schemaVersion\":0,\"settings\":{\"flushIntervalSeconds\":7,\"logStoragePath\":\"" + Escape(fixture.Root + "\\logs") + "\",\"monitoringPaths\":[\"" + Escape(fixture.Root) + "\"],\"unknownFutureField\":true},\"unknownRootField\":42}", new UTF8Encoding(false));

        var result = fixture.Machine.Load();

        Assert.Equal(7, result.Settings.FlushIntervalSeconds);
        Assert.Contains("migrated", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CorruptPrimaryUsesPreviousGeneration()
    {
        using var fixture = new SettingsFixture();
        var first = new UserSettings { EventStackPageSize = 100 };
        var second = new UserSettings { EventStackPageSize = 200 };
        fixture.User.Save(first);
        fixture.User.Save(second);
        File.WriteAllText(fixture.UserPath, "{broken", new UTF8Encoding(false));

        var result = fixture.User.Load();

        Assert.Equal(first.EventStackPageSize, result.Settings.EventStackPageSize);
        Assert.True(result.Recovered);
        Assert.True(result.UsedPreviousVersion);
    }

    [Fact]
    public void BothCorruptGenerationsRecoverToDefaultsWithWarning()
    {
        using var fixture = new SettingsFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.UserPath)!);
        File.WriteAllText(fixture.UserPath, "bad", new UTF8Encoding(false));
        File.WriteAllText(fixture.UserPath + ".bak", "bad", new UTF8Encoding(false));

        var result = fixture.User.Load();

        Assert.Equal(DefaultSettings.CreateUser(), result.Settings);
        Assert.True(result.Recovered);
        Assert.False(result.UsedPreviousVersion);
        Assert.Contains("defaults", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AtomicReplaceFailureLeavesPreviousFileAndCleansTemporaryFile()
    {
        using var fixture = new SettingsFixture();
        fixture.User.Save(new UserSettings { EventStackPageSize = 100 });
        fixture.FileSystem.FailReplace = true;

        Assert.Throws<IOException>(() => fixture.User.Save(new UserSettings { EventStackPageSize = 200 }));
        Assert.Equal(100, fixture.User.Load().Settings.EventStackPageSize);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void ValidationCoversRangesPathsAndMirrors()
    {
        var machine = new MachineSettings
        {
            MonitoringPaths = new[] { "relative", "C:\\valid" },
            ExcludedPaths = new[] { "C:\\bad*path" },
            LogStoragePath = "",
            MediaMirrors = new Dictionary<string, string> { [""] = "relative" },
            FlushIntervalSeconds = 61
        };
        var user = new UserSettings { ActivityGroupTimeoutSeconds = 0.4, EventStackPageSize = 49, DiffZoomPercent = 301 };

        var machineResult = SettingsValidator.Validate(machine);
        var userResult = SettingsValidator.Validate(user);

        Assert.False(machineResult.IsValid);
        Assert.Contains(machineResult.Errors, error => error.Property == "FlushIntervalSeconds");
        Assert.Contains(machineResult.Errors, error => error.Property == "LogStoragePath");
        Assert.False(userResult.IsValid);
        Assert.Equal(3, userResult.Errors.Count);
    }

    [Fact]
    public async Task AgentRejectsUnauthorizedChangeWithoutWritingOrHistory()
    {
        using var fixture = new SettingsFixture();
        var history = new RecordingHistory();
        var service = new AgentSettingsService(fixture.Machine, fixture.User, history, new DenyAuthorizer(), new RecordingLifecycle());

        var result = await service.ApplyMachineSettingsAsync(fixture.ValidMachine with { FlushIntervalSeconds = 10 }, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Empty(history.Events);
        Assert.False(File.Exists(fixture.MachinePath));
    }

    [Fact]
    public async Task AgentRecordsChangedPropertiesAndSafelyRestarts()
    {
        using var fixture = new SettingsFixture();
        var history = new RecordingHistory();
        var lifecycle = new RecordingLifecycle();
        var service = new AgentSettingsService(fixture.Machine, fixture.User, history, new AllowAllAgentSettingsAuthorizer(), lifecycle, () => DateTimeOffset.UnixEpoch);

        var result = await service.ApplyMachineSettingsAsync(fixture.ValidMachine with { FlushIntervalSeconds = 10 }, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(result.RestartRequired);
        Assert.True(lifecycle.Restarted);
        var change = Assert.Single(history.Events);
        Assert.Equal("Machine", change.Scope);
        Assert.Contains(nameof(MachineSettings.FlushIntervalSeconds), change.ChangedProperties);
        Assert.Equal(DateTimeOffset.UnixEpoch, change.ChangedAtUtc);
    }

    [Fact]
    public async Task AgentRestoresPreviousSettingsWhenRestartFails()
    {
        using var fixture = new SettingsFixture();
        var previous = fixture.ValidMachine with { FlushIntervalSeconds = 5 };
        fixture.Machine.Save(previous);
        var service = new AgentSettingsService(fixture.Machine, fixture.User, new RecordingHistory(), new AllowAllAgentSettingsAuthorizer(), new FailingLifecycle());

        var result = await service.ApplyMachineSettingsAsync(previous with { FlushIntervalSeconds = 10 }, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(5, fixture.Machine.Load().Settings.FlushIntervalSeconds);
    }

    [Fact]
    public async Task AgentHonorsCancellationBeforePersistence()
    {
        using var fixture = new SettingsFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new AgentSettingsService(fixture.Machine, fixture.User, new RecordingHistory(), new AllowAllAgentSettingsAuthorizer(), new RecordingLifecycle());

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ApplyMachineSettingsAsync(fixture.ValidMachine, cancellation.Token).AsTask());

        Assert.False(File.Exists(fixture.MachinePath));
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private sealed class SettingsFixture : IDisposable
    {
        private readonly string tempDirectory = Path.Combine(Path.GetTempPath(), "StorageChronicleSettingsTests", Guid.NewGuid().ToString("N"));
        public SettingsFixture()
        {
            Directory.CreateDirectory(tempDirectory);
            var paths = new WindowsSettingsPathProvider(tempDirectory, tempDirectory);
            FileSystem = new RecordingFileSystem();
            Machine = new MachineSettingsStore(paths, FileSystem);
            User = new UserSettingsStore(paths, FileSystem);
        }
        public string Root => tempDirectory;
        public string MachinePath => Path.Combine(tempDirectory, "Storage Chronicle", "config", "machine-settings.json");
        public string UserPath => Path.Combine(tempDirectory, "Storage Chronicle", "user-settings.json");
        public RecordingFileSystem FileSystem { get; }
        public MachineSettingsStore Machine { get; }
        public UserSettingsStore User { get; }
        public MachineSettings ValidMachine => new() { MonitoringPaths = new[] { Root }, LogStoragePath = Root, FlushIntervalSeconds = 5 };
        public byte[] ReadBytes(string path) => File.ReadAllBytes(path);
        public void Dispose() => Directory.Delete(tempDirectory, recursive: true);
    }

    private sealed class RecordingFileSystem : ISettingsFileSystem
    {
        private readonly PhysicalSettingsFileSystem inner = new();
        public bool FailReplace { get; set; }
        public bool FileExists(string path) => inner.FileExists(path);
        public string ReadUtf8(string path) => inner.ReadUtf8(path);
        public void WriteUtf8Flushed(string path, string content) => inner.WriteUtf8Flushed(path, content);
        public void ReplaceAtomically(string temporaryPath, string targetPath, string backupPath)
        {
            if (FailReplace) throw new IOException("Injected replace failure.");
            inner.ReplaceAtomically(temporaryPath, targetPath, backupPath);
        }
        public void MoveAtomically(string temporaryPath, string targetPath) => inner.MoveAtomically(temporaryPath, targetPath);
        public void DeleteIfExists(string path) => inner.DeleteIfExists(path);
    }

    private sealed class RecordingHistory : ISettingsChangeHistory
    {
        public List<SettingsChangeHistoryEvent> Events { get; } = new();
        public ValueTask RecordAsync(SettingsChangeHistoryEvent value, CancellationToken cancellationToken = default)
        {
            Events.Add(value);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingLifecycle : IMonitoringLifecycle
    {
        public bool Restarted { get; private set; }
        public ValueTask RestartAsync(CancellationToken cancellationToken = default)
        {
            Restarted = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DenyAuthorizer : IAgentSettingsAuthorizer
    {
        public bool CanApplyMachineSettings(MachineSettings settings) => false;
    }

    private sealed class FailingLifecycle : IMonitoringLifecycle
    {
        public ValueTask RestartAsync(CancellationToken cancellationToken = default) => ValueTask.FromException(new IOException("restart failed"));
    }
}
