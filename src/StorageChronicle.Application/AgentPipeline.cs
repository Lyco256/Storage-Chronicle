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
    private readonly IReadOnlyList<ICanonicalEventSink> sinks;
    private readonly Channel<SourceEvent> queue;
    private readonly HashSet<EventId> seenEventIds = [];
    private readonly Queue<EventId> seenEventOrder = [];
    private readonly object seenGate = new();
    private readonly int capacity;
    private int queuedEvents;

    /// <summary>Initializes a bounded pipeline.</summary>
    public AgentPipeline(IEventStore eventStore, IStateStore stateStore, IEventNormalizer normalizer, int capacity = 4096, IEnumerable<ICanonicalEventSink>? sinks = null)
    {
        this.eventStore = eventStore;
        this.stateStore = stateStore;
        this.normalizer = normalizer;
        this.sinks = sinks?.ToArray() ?? Array.Empty<ICanonicalEventSink>();
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.capacity = capacity;
        queue = Channel.CreateBounded<SourceEvent>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    }

    /// <summary>Maximum in-flight source events.</summary>
    public int Capacity => capacity;

    /// <summary>Raised when one collector fails; other collectors remain supervised and continue.</summary>
    public event Action<ISourceEventCollector, Exception>? CollectorFailed;

    /// <summary>Raised after one source fact enters the bounded durable pipeline.</summary>
    public event Action<SourceEvent>? SourceObserved;

    /// <summary>Raised when an optional secondary sink fails after primary durability.</summary>
    public event Action<ICanonicalEventSink, Exception>? SinkFailed;

    /// <summary>Raised when the bounded queue depth changes.</summary>
    public event Action<int>? QueueDepthChanged;

    /// <summary>Processes a collector until cancellation or completion.</summary>
    public async Task RunAsync(ISourceEventCollector collector, CancellationToken cancellationToken = default)
        => await RunAsync(new[] { collector }, cancellationToken).ConfigureAwait(false);

    /// <summary>Processes multiple collectors through one bounded durable pipeline.</summary>
    public async Task RunAsync(IReadOnlyList<ISourceEventCollector> collectors, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collectors);
        if (collectors.Count == 0) throw new ArgumentException("At least one collector is required.", nameof(collectors));
        var producers = collectors.Select(collector => ProduceAsync(collector, cancellationToken)).ToArray();
        var producerCompletion = CompleteProducersAsync(producers);
        try
        {
            await foreach (var source in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                QueueDepthChanged?.Invoke(Math.Max(0, Interlocked.Decrement(ref queuedEvents)));
                if (!RememberEventId(source.EventId)) continue;
                var canonical = normalizer.Normalize(source);
                // Read-only ETW observations and clipboard candidates are deliberately
                // transient correlation facts. They must reach the normalizer but never
                // enter the durable Source Event log when no canonical change resulted.
                if (canonical is null) continue;
                SourceObserved?.Invoke(source);
                await eventStore.AppendSourceAsync(source, cancellationToken).ConfigureAwait(false);
                await eventStore.AppendCanonicalAsync(canonical, cancellationToken).ConfigureAwait(false);
                await stateStore.ApplyAsync(canonical, cancellationToken).ConfigureAwait(false);
                foreach (var sink in sinks)
                {
                    try
                    {
                        await sink.OnCanonicalAsync(canonical, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        SinkFailed?.Invoke(sink, exception);
                    }
                }
            }
            await producerCompletion.ConfigureAwait(false);
        }
        finally
        {
            try { await producerCompletion.ConfigureAwait(false); } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            queue.Writer.TryComplete();
            Interlocked.Exchange(ref queuedEvents, 0);
            QueueDepthChanged?.Invoke(0);
        }
    }

    private bool RememberEventId(EventId eventId)
    {
        lock (seenGate)
        {
            if (!seenEventIds.Add(eventId)) return false;
            seenEventOrder.Enqueue(eventId);
            while (seenEventOrder.Count > capacity)
            {
                seenEventIds.Remove(seenEventOrder.Dequeue());
            }

            return true;
        }
    }

    private async Task CompleteProducersAsync(IReadOnlyList<Task> producers)
    {
        await Task.WhenAll(producers).ConfigureAwait(false);
        queue.Writer.TryComplete();
    }

    private async Task ProduceAsync(ISourceEventCollector collector, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var value in collector.CollectAsync(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                QueueDepthChanged?.Invoke(Interlocked.Increment(ref queuedEvents));
                try
                {
                    await queue.Writer.WriteAsync(value, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    QueueDepthChanged?.Invoke(Math.Max(0, Interlocked.Decrement(ref queuedEvents)));
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            CollectorFailed?.Invoke(collector, exception);
        }
    }
}
