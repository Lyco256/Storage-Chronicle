using Microsoft.Extensions.Hosting;
using StorageChronicle.Application;
using StorageChronicle.Contracts;
using StorageChronicle.Platform.Abstractions;

namespace StorageChronicle.Agent;

/// <summary>Hosted service entry point; collector failures are supervised without stopping other collectors.</summary>
public sealed class AgentWorker(AgentPipeline pipeline, ISourceEventCollector collector) : BackgroundService
{
    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => pipeline.RunAsync(collector, stoppingToken);
}
