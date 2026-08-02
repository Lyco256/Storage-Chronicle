using System.Threading.Channels;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Application;

/// <summary>Runs the bounded source→canonical→state pipeline and isolates collector failures.</summary>
public sealed class AgentPipeline
{
    private readonly IEventStore eventStore;
    private readonly IStateStore stateStore;
    private readonly IEventNormalizer normalizer;
    private readonly Channel<SourceEvent> queue;
    private readonly int capacity;

    /// <summary>Initializes a bounded pipeline.</summary>
    public AgentPipeline(IEventStore eventStore, IStateStore stateStore, IEventNormalizer normalizer, int capacity = 4096)
    {
        this.eventStore = eventStore;
        this.stateStore = stateStore;
        this.normalizer = normalizer;
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.capacity = capacity;
        queue = Channel.CreateBounded<SourceEvent>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    }

    /// <summary>Maximum in-flight source events.</summary>
    public int Capacity => capacity;

    /// <summary>Processes a collector until cancellation or completion.</summary>
    public async Task RunAsync(ISourceEventCollector collector, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collector);
        var producer = ProduceAsync(collector, cancellationToken);
        try
        {
            await foreach (var source in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await eventStore.AppendSourceAsync(source, cancellationToken).ConfigureAwait(false);
                var canonical = normalizer.Normalize(source);
                if (canonical is null) continue;
                await eventStore.AppendCanonicalAsync(canonical, cancellationToken).ConfigureAwait(false);
                await stateStore.ApplyAsync(canonical, cancellationToken).ConfigureAwait(false);
            }
            await producer.ConfigureAwait(false);
        }
        finally
        {
            queue.Writer.TryComplete();
        }
    }

    private async Task ProduceAsync(ISourceEventCollector collector, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var value in collector.CollectAsync(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await queue.Writer.WriteAsync(value, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            queue.Writer.TryComplete();
        }
    }
}
