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
    public AgentWorker(IEventStore eventStore, IStateStore stateStore, IEventNormalizer normalizer, IEnumerable<ISourceEventCollector> collectors, AppendOnlyStorageEngine storage, AgentHealthState health, IEnumerable<ICanonicalEventSink>? sinks = null)
    {
        this.eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        this.stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        this.normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        this.collectors = collectors?.ToArray() ?? throw new ArgumentNullException(nameof(collectors));
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        this.health = health ?? throw new ArgumentNullException(nameof(health));
        this.sinks = sinks?.ToArray() ?? Array.Empty<ICanonicalEventSink>();
    }

    private readonly IEventStore eventStore;
    private readonly IStateStore stateStore;
    private readonly IEventNormalizer normalizer;
    private readonly IReadOnlyList<ISourceEventCollector> collectors;
    private readonly AppendOnlyStorageEngine storage;
    private readonly AgentHealthState health;
    private readonly IReadOnlyList<ICanonicalEventSink> sinks;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly object runGate = new();
    private CancellationTokenSource? activeRun;
    private TaskCompletionSource<bool>? activeCompletion;
    private int restartRequested;

    /// <summary>Reports an isolated collector failure as Agent health without stopping unrelated collectors.</summary>
    public event Action<Exception>? CollectorFailure;

    /// <summary>Safely cancels the current collection pass, flushes the log, and starts a fresh pass.</summary>
    public async ValueTask RestartAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref restartRequested, 1);
            Task? completion;
            lock (runGate)
            {
                activeRun?.Cancel();
                completion = activeCompletion?.Task;
            }

            if (completion is not null) await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            await FlushSinksAsync(cancellationToken).ConfigureAwait(false);
            await storage.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (runGate)
                {
                    activeRun = runCancellation;
                    activeCompletion = completion;
                }

                try
                {
                    health.ClearFailure();
                    var pipeline = new AgentPipeline(eventStore, stateStore, normalizer, sinks: sinks);
                    pipeline.CollectorFailed += OnCollectorFailed;
                    pipeline.SourceObserved += health.Observe;
                    pipeline.SinkFailed += health.RecordSinkFailure;
                    pipeline.QueueDepthChanged += health.SetQueueDepth;
                    await pipeline.RunAsync(collectors, runCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
                {
                }
                finally
                {
                    lock (runGate)
                    {
                        activeRun = null;
                        activeCompletion = null;
                    }
                    completion.TrySetResult(true);
                    health.SetQueueDepth(0);
                    runCancellation.Dispose();
                }

                if (stoppingToken.IsCancellationRequested) break;
                if (Interlocked.Exchange(ref restartRequested, 0) == 0)
                {
                    // A finite collector is allowed to finish, but the service remains
                    // alive and gives the supervisor a chance to restart it on settings change.
                    await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            lock (runGate) activeRun?.Cancel();
            foreach (var disposable in collectors.OfType<IAsyncDisposable>())
            {
                try { await disposable.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
            }
            await FlushSinksAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (var sink in sinks)
            {
                try { await sink.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            }
            await storage.StopAsync(CancellationToken.None).ConfigureAwait(false);
            lifecycleGate.Dispose();
        }
    }

    private void OnCollectorFailed(ISourceEventCollector collector, Exception exception)
    {
        health.RecordCollectorFailure(collector, exception);
        CollectorFailure?.Invoke(exception);
    }

    private async ValueTask FlushSinksAsync(CancellationToken cancellationToken)
    {
        foreach (var sink in sinks)
        {
            try { await sink.FlushAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { health.RecordSinkFailure(sink, exception); }
        }
    }
}
