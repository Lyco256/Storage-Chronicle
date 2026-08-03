using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StorageChronicle.Application;
using StorageChronicle.Contracts;
using StorageChronicle.Normalization;
using StorageChronicle.Platform.Windows.FileSystem;
using StorageChronicle.Platform.Windows.FileSystem.Policy;
using StorageChronicle.Platform.Windows.FileSystem.Volumes;
using StorageChronicle.Platform.Windows.Ntfs;
using StorageChronicle.Projection;
using StorageChronicle.Settings;
using StorageChronicle.Storage;
using StorageChronicle.ExternalMedia;

namespace StorageChronicle.Agent;

/// <summary>Builds the LocalSystem-compatible Windows Service host.</summary>
public static class Program
{
    /// <summary>Starts the LocalSystem service with durable storage and the Windows filesystem collector.</summary>
    public static Task Main(string[] args)
    {
        _ = WindowsServiceRecoveryConfigurator.TryConfigure();
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options => options.ServiceName = "Storage Chronicle Agent");
        var productRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Storage Chronicle");
        var defaultHistoryRoot = Path.Combine(productRoot, "history");
        var machineSettingsStore = new MachineSettingsStore();
        var initialMachineSettings = machineSettingsStore.Load().Settings;
        var storageDirectory = string.IsNullOrWhiteSpace(initialMachineSettings.LogStoragePath) ? defaultHistoryRoot : Path.GetFullPath(initialMachineSettings.LogStoragePath);
        var flushSeconds = Math.Clamp(initialMachineSettings.FlushIntervalSeconds, 1, 60);
        builder.Services.AddSingleton(new AppendOnlyStorageEngine(new StorageEngineOptions(storageDirectory) { FlushInterval = TimeSpan.FromSeconds(flushSeconds) }));
        builder.Services.AddSingleton<AgentHealthState>();
        builder.Services.AddSingleton<IEventStore>(services => services.GetRequiredService<AppendOnlyStorageEngine>());
        builder.Services.AddSingleton<IStateStore>(services => services.GetRequiredService<AppendOnlyStorageEngine>());
        builder.Services.AddSingleton<IEventNormalizer, EventNormalizer>();
        builder.Services.AddSingleton(machineSettingsStore);
        builder.Services.AddSingleton<ISettingsStore<MachineSettings>>(services => services.GetRequiredService<MachineSettingsStore>());
        builder.Services.AddSingleton<ISettingsStore<UserSettings>, UserSettingsStore>();
        builder.Services.AddSingleton<ISettingsChangeHistory, SettingsHistoryStore>();
        builder.Services.AddSingleton<IAgentSettingsAuthorizer, NamedPipeSettingsAuthorizer>();
        builder.Services.AddSingleton<AgentWorker>();
        builder.Services.AddSingleton<IMonitoringLifecycle, AgentMonitoringLifecycle>();
        builder.Services.AddSingleton<AgentSettingsService>();
        builder.Services.AddSingleton<IAgentSettingsGateway>(services => services.GetRequiredService<AgentSettingsService>());
        builder.Services.AddSingleton<IVolumeEnumerator, WindowsVolumeEnumerator>();
        builder.Services.AddSingleton<INtfsApi, WindowsNtfsApi>();
        builder.Services.AddSingleton(new WindowsExclusionPolicy(new WindowsFileSystemOptions
        {
            StorageChronicleDataRoot = productRoot,
            UserExcludedRoots = initialMachineSettings.ExcludedPaths,
            MonitoredRoots = initialMachineSettings.MonitoringPaths
        }));
        builder.Services.AddSingleton<IMediaMonitoringExclusionRegistrar, WindowsMediaExclusionRegistrar>();
        builder.Services.AddSingleton<ExternalMediaMirrorCoordinator>(services => new ExternalMediaMirrorCoordinator(
            services.GetRequiredService<ISettingsStore<MachineSettings>>(), Environment.MachineName,
            services.GetRequiredService<IMediaMonitoringExclusionRegistrar>()));
        builder.Services.AddSingleton<IMediaMirrorSessionCoordinator>(services => services.GetRequiredService<ExternalMediaMirrorCoordinator>());
        builder.Services.AddSingleton<ICanonicalEventSink>(services => services.GetRequiredService<ExternalMediaMirrorCoordinator>());
        builder.Services.AddSingleton<IExternalMediaChangeSource, WindowsExternalMediaChangeSource>();
        builder.Services.AddSingleton<ISourceEventCollector>(services => new WindowsFileSystemCollector(
            services.GetRequiredService<IVolumeEnumerator>(),
            exclusionPolicy: services.GetRequiredService<WindowsExclusionPolicy>(),
            options: new WindowsFileSystemOptions
            {
                StorageChronicleDataRoot = productRoot,
                UserExcludedRoots = initialMachineSettings.ExcludedPaths,
                MonitoredRoots = initialMachineSettings.MonitoringPaths
            }));
        builder.Services.AddSingleton<ISourceEventCollector>(services => new WindowsNtfsVolumeCollector(
            services.GetRequiredService<IVolumeEnumerator>(),
            services.GetRequiredService<INtfsApi>(),
            exclusionPolicy: services.GetRequiredService<WindowsExclusionPolicy>()));
        builder.Services.AddSingleton<ISourceEventCollector, WindowsEtwFileIoCollector>();
        builder.Services.AddSingleton<ISourceEventCollector, WindowsShareCollector>();
        builder.Services.AddSingleton<ISourceEventCollector>(services => new WindowsExternalMediaCollector(
            services.GetRequiredService<IExternalMediaChangeSource>(),
            services.GetRequiredService<IVolumeEnumerator>(),
            services.GetRequiredService<ISettingsStore<MachineSettings>>(),
            Environment.MachineName,
            mirrorCoordinator: services.GetRequiredService<IMediaMirrorSessionCoordinator>()));
        builder.Services.AddSingleton<IProjectionService, AgentProjectionService>();
        builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(services => services.GetRequiredService<AgentWorker>());
        builder.Services.AddHostedService<NamedPipeAgentServer>();
        return builder.Build().RunAsync();
    }
}
