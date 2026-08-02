using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace StorageChronicle.Agent;

/// <summary>Builds the LocalSystem-compatible Windows Service host.</summary>
public static class Program
{
    /// <summary>Starts the service host; the platform-specific collector composition is supplied by deployment.</summary>
    public static Task Main(string[] args) => Host.CreateApplicationBuilder(args).Build().RunAsync();
}
