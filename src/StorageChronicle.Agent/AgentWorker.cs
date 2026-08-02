using Microsoft.Extensions.Hosting;
using StorageChronicle.Application;
using StorageChronicle.Contracts;
using StorageChronicle.Platform.Abstractions;
using StorageChronicle.Storage;

namespace StorageChronicle.Agent;

/// <summary>Hosted service entry point; collector failures are supervised without stopping other collectors.</summary>
public sealed class AgentWorker : BackgroundService
{
    /// <summary>Initializes the supervised Agent worker.</summary>
    public AgentWorker(AgentPipeline pipeline, IEnumerable<ISourceEventCollector> collectors, AppendOnlyStorageEngine storage)
    {
        this.pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        this.collectors = collectors?.ToArray() ?? throw new ArgumentNullException(nameof(collectors));
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        pipeline.CollectorFailed += OnCollectorFailed;
    }

    private readonly AgentPipeline pipeline;
    private readonly IReadOnlyList<ISourceEventCollector> collectors;
    private readonly AppendOnlyStorageEngine storage;

    /// <summary>Reports an isolated collector failure as Agent health without stopping unrelated collectors.</summary>
    public event Action<Exception>? CollectorFailure;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await pipeline.RunAsync(collectors, stoppingToken).ConfigureAwait(false); }
        finally { await storage.StopAsync(CancellationToken.None).ConfigureAwait(false); }
    }

    private void OnCollectorFailed(ISourceEventCollector _, Exception exception) => CollectorFailure?.Invoke(exception);
}
