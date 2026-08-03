using System.Text.Json;
using StorageChronicle.Platform.Windows.FileSystem.Policy;
using StorageChronicle.Settings;
using StorageChronicle.Storage;

namespace StorageChronicle.Agent;

/// <summary>Carries the impersonated named-pipe authorization decision through an async settings operation.</summary>
internal static class SettingsAuthorizationContext
{
    private static readonly AsyncLocal<bool> Administrator = new();

    /// <summary>Returns whether the current IPC operation has an administrator token.</summary>
    public static bool IsAdministrator => Administrator.Value;

    /// <summary>Sets an operation-scoped authorization value and restores the prior value on disposal.</summary>
    public static IDisposable Enter(bool isAdministrator)
    {
        var previous = Administrator.Value;
        Administrator.Value = isAdministrator;
        return new Scope(() => Administrator.Value = previous);
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) dispose();
        }
    }
}

/// <summary>Allows machine settings only when the named-pipe client was verified as an administrator.</summary>
public sealed class NamedPipeSettingsAuthorizer : IAgentSettingsAuthorizer
{
    /// <inheritdoc />
    public bool CanApplyMachineSettings(MachineSettings settings) => SettingsAuthorizationContext.IsAdministrator;
}

/// <summary>Persists settings change facts as append-only UTF-8 JSON without storing values or secrets.</summary>
public sealed class SettingsHistoryStore : ISettingsChangeHistory, IDisposable
{
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>Initializes the history file below the product's machine data directory.</summary>
    public SettingsHistoryStore(string? path = null)
    {
        this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Storage Chronicle", "history", "settings-history.ndjson");
    }

    /// <inheritdoc />
    public async ValueTask RecordAsync(SettingsChangeHistoryEvent value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var directory = Path.GetDirectoryName(path) ?? throw new IOException("Settings history path has no directory.");
        Directory.CreateDirectory(directory);
        var line = JsonSerializer.Serialize(value) + Environment.NewLine;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous);
            await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
            await writer.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Releases the serialized history writer gate.</summary>
    public void Dispose() => gate.Dispose();
}

/// <summary>Delegates a settings-triggered restart to the hosted monitoring supervisor.</summary>
public sealed class AgentMonitoringLifecycle : IMonitoringLifecycle
{
    private readonly AgentWorker worker;
    private readonly ISettingsStore<MachineSettings>? machineSettings;
    private readonly WindowsExclusionPolicy? exclusionPolicy;
    private readonly AppendOnlyStorageEngine? storage;

    /// <summary>Initializes the lifecycle adapter.</summary>
    public AgentMonitoringLifecycle(AgentWorker worker, ISettingsStore<MachineSettings>? machineSettings = null, WindowsExclusionPolicy? exclusionPolicy = null, AppendOnlyStorageEngine? storage = null)
    {
        this.worker = worker ?? throw new ArgumentNullException(nameof(worker));
        this.machineSettings = machineSettings!;
        this.exclusionPolicy = exclusionPolicy;
        this.storage = storage;
    }

    /// <inheritdoc />
    public async ValueTask RestartAsync(CancellationToken cancellationToken = default)
    {
        if (machineSettings is not null)
        {
            var current = machineSettings.Load().Settings;
            exclusionPolicy?.SetUserExcludedRoots(current.ExcludedPaths);
            exclusionPolicy?.SetMonitoredRoots(current.MonitoringPaths);
            storage?.UpdateFlushInterval(TimeSpan.FromSeconds(Math.Clamp(current.FlushIntervalSeconds, 1, 60)));
            if (storage is not null && !string.Equals(storage.StorageDirectory, Path.GetFullPath(current.LogStoragePath), StringComparison.OrdinalIgnoreCase))
            {
                await storage.RelocateAsync(current.LogStoragePath, cancellationToken).ConfigureAwait(false);
            }
        }

        if (storage is not null) await storage.TryResumeAsync(cancellationToken).ConfigureAwait(false);
        await worker.RestartAsync(cancellationToken).ConfigureAwait(false);
    }
}
