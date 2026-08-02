using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StorageChronicle.Application;
using StorageChronicle.Contracts;
using StorageChronicle.Normalization;
using StorageChronicle.Platform.Windows.FileSystem;
using StorageChronicle.Projection;
using StorageChronicle.Settings;
using StorageChronicle.Storage;

namespace StorageChronicle.Agent;

/// <summary>Builds the LocalSystem-compatible Windows Service host.</summary>
public static class Program
{
    /// <summary>Starts the LocalSystem service with durable storage and the Windows filesystem collector.</summary>
    public static Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options => options.ServiceName = "Storage Chronicle Agent");
        var storageDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Storage Chronicle", "history");
        builder.Services.AddSingleton(new AppendOnlyStorageEngine(new StorageEngineOptions(storageDirectory)));
        builder.Services.AddSingleton<IEventStore>(services => services.GetRequiredService<AppendOnlyStorageEngine>());
        builder.Services.AddSingleton<IStateStore>(services => services.GetRequiredService<AppendOnlyStorageEngine>());
        builder.Services.AddSingleton<IEventNormalizer, EventNormalizer>();
        builder.Services.AddSingleton<ISettingsStore<MachineSettings>, MachineSettingsStore>();
        builder.Services.AddSingleton<ISettingsStore<UserSettings>, UserSettingsStore>();
        builder.Services.AddSingleton<ISettingsChangeHistory, SettingsHistoryStore>();
        builder.Services.AddSingleton<IAgentSettingsAuthorizer, NamedPipeSettingsAuthorizer>();
        builder.Services.AddSingleton<IMonitoringLifecycle, StorageFlushMonitoringLifecycle>();
        builder.Services.AddSingleton<AgentSettingsService>();
        builder.Services.AddSingleton<IAgentSettingsGateway>(services => services.GetRequiredService<AgentSettingsService>());
        builder.Services.AddSingleton<ISourceEventCollector, WindowsFileSystemCollector>();
        builder.Services.AddSingleton<IProjectionService, AgentProjectionService>();
        builder.Services.AddSingleton<AgentPipeline>();
        builder.Services.AddHostedService<AgentWorker>();
        builder.Services.AddHostedService<NamedPipeAgentServer>();
        return builder.Build().RunAsync();
    }
}
