using System.Collections.Concurrent;
using System.Threading.Channels;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Abstractions;
using StorageChronicle.Storage;

namespace StorageChronicle.Agent;

/// <summary>Keeps bounded, process-instance exit times in memory for Activity Frame lifecycle projections.</summary>
public sealed class AgentProcessLifecycleState : IProcessLifecycleSink, IAsyncDisposable
{
    private const int Capacity = 8192;
    private readonly ConcurrentDictionary<ProcessInstanceId, DateTimeOffset> exitTimes = new();
    private readonly ConcurrentQueue<ProcessInstanceId> insertionOrder = new();
    private readonly object writeGate = new();
    private readonly AppendOnlyStorageEngine? storage;
    private readonly AgentHealthState? health;
    private readonly Channel<ProcessLifecycleEvent>? persistenceQueue;
    private readonly Task? persistenceWorker;
    private Exception? persistenceFailure;

    /// <summary>Initializes a bounded lifecycle cache with an optional durable product-history writer.</summary>
    public AgentProcessLifecycleState(AppendOnlyStorageEngine? storage = null, AgentHealthState? health = null)
    {
        this.storage = storage;
        this.health = health;
        if (storage is not null)
        {
            persistenceQueue = Channel.CreateBounded<ProcessLifecycleEvent>(new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            persistenceWorker = Task.Run(PersistLifecycleEventsAsync);
        }
    }

    /// <inheritdoc />
    public void RecordProcessExit(ProcessInstanceId processInstanceId, DateTimeOffset exitedUtc)
    {
        if (string.IsNullOrWhiteSpace(processInstanceId.Value)) throw new ArgumentException("A process instance identity is required.", nameof(processInstanceId));
        if (exitedUtc == default) throw new ArgumentException("A process exit time is required.", nameof(exitedUtc));
        lock (writeGate)
        {
            if (exitTimes.TryGetValue(processInstanceId, out var existing) && exitedUtc >= existing) return;
            if (storage is not null && persistenceQueue is not null)
            {
                if (Volatile.Read(ref persistenceFailure) is { } priorFailure)
                    throw new StorageException("Process lifecycle history persistence has failed.", priorFailure);

                var lifecycleEvent = new ProcessLifecycleEvent(
                    EventId.New(),
                    EventSchemaVersion.Current,
                    processInstanceId,
                    ProcessLifecycleTransition.Exited,
                    exitedUtc.ToUniversalTime(),
                    DateTimeOffset.UtcNow,
                    EventOrigin.Etw,
                    EventQuality.Exact);
                if (!persistenceQueue.Writer.TryWrite(lifecycleEvent))
                {
                    var exception = new StorageException("The bounded process lifecycle persistence queue is full or closed.");
                    health?.RecordPipelineFailure(exception);
                    throw exception;
                }
            }

            if (exitTimes.TryAdd(processInstanceId, exitedUtc)) insertionOrder.Enqueue(processInstanceId);
            else exitTimes[processInstanceId] = exitedUtc;
            while (exitTimes.Count > Capacity && insertionOrder.TryDequeue(out var oldest)) exitTimes.TryRemove(oldest, out _);
        }
    }

    /// <inheritdoc />
    public bool TryGetProcessExit(ProcessInstanceId processInstanceId, out DateTimeOffset exitedUtc) => exitTimes.TryGetValue(processInstanceId, out exitedUtc);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        persistenceQueue?.Writer.TryComplete();
        if (persistenceWorker is not null) await persistenceWorker.ConfigureAwait(false);
    }

    private async Task PersistLifecycleEventsAsync()
    {
        if (persistenceQueue is null || storage is null) return;
        try
        {
            await foreach (var lifecycleEvent in persistenceQueue.Reader.ReadAllAsync().ConfigureAwait(false))
                await storage.AppendProcessLifecycleEventAsync(lifecycleEvent, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            Volatile.Write(ref persistenceFailure, exception);
            health?.RecordPipelineFailure(exception);
            persistenceQueue.Writer.TryComplete(exception);
        }
    }
}
