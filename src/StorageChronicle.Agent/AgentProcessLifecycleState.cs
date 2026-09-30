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
    private readonly SemaphoreSlim retryGate = new(1, 1);
    private readonly AppendOnlyStorageEngine? storage;
    private readonly AgentHealthState? health;
    private readonly Queue<ProcessLifecycleEvent> retryQueue = new();
    private Channel<ProcessLifecycleEvent>? persistenceQueue;
    private Task? persistenceWorker;
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
                var lifecycleEvent = new ProcessLifecycleEvent(
                    EventId.New(),
                    EventSchemaVersion.Current,
                    processInstanceId,
                    ProcessLifecycleTransition.Exited,
                    exitedUtc.ToUniversalTime(),
                    DateTimeOffset.UtcNow,
                    EventOrigin.Etw,
                    EventQuality.Exact);
                if (Volatile.Read(ref persistenceFailure) is { } priorFailure)
                {
                    if (retryQueue.Count >= Capacity)
                    {
                        var exception = new StorageException("The bounded process lifecycle retry queue is full; the latest process exit fact could not be retained.", priorFailure);
                        health?.RecordPipelineFailure(exception);
                        throw exception;
                    }
                    retryQueue.Enqueue(lifecycleEvent);
                    UpdateExitCache(processInstanceId, exitedUtc);
                    throw new StorageException("Process lifecycle history is stopped; the process exit fact is retained for an explicit persistence retry.", priorFailure);
                }

                if (!persistenceQueue.Writer.TryWrite(lifecycleEvent))
                {
                    var exception = new StorageException("The bounded process lifecycle persistence queue is full or closed.");
                    health?.RecordPipelineFailure(exception);
                    throw exception;
                }
            }

            UpdateExitCache(processInstanceId, exitedUtc);
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

    /// <summary>Retries retained lifecycle facts after the storage engine has resumed.</summary>
    /// <returns><see langword="true"/> when every retained fact is durable; otherwise <see langword="false"/>.</returns>
    public async ValueTask<bool> RetryPendingAsync(CancellationToken cancellationToken = default)
    {
        if (storage is null) return true;
        await retryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (writeGate)
            {
                // A storage stop unrelated to lifecycle history does not require a
                // replacement queue; keep the existing live worker intact.
                if (persistenceFailure is null && retryQueue.Count == 0) return true;
            }

            while (true)
            {
                ProcessLifecycleEvent? pending;
                lock (writeGate) pending = retryQueue.Count == 0 ? null : retryQueue.Peek();
                if (pending is null)
                {
                    lock (writeGate)
                    {
                        if (retryQueue.Count != 0) continue;
                        persistenceQueue = CreatePersistenceQueue();
                        persistenceFailure = null;
                        persistenceWorker = Task.Run(PersistLifecycleEventsAsync, CancellationToken.None);
                        return true;
                    }
                }

                try
                {
                    await storage.AppendProcessLifecycleEventAsync(pending, cancellationToken).ConfigureAwait(false);
                    lock (writeGate)
                    {
                        if (retryQueue.Count > 0 && retryQueue.Peek().EventId == pending.EventId) retryQueue.Dequeue();
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
                {
                    health?.RecordPipelineFailure(exception);
                    return false;
                }
            }
        }
        finally
        {
            retryGate.Release();
        }
    }

    private async Task PersistLifecycleEventsAsync()
    {
        if (persistenceQueue is null || storage is null) return;
        ProcessLifecycleEvent? inFlight = null;
        try
        {
            await foreach (var lifecycleEvent in persistenceQueue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                inFlight = lifecycleEvent;
                await storage.AppendProcessLifecycleEventAsync(lifecycleEvent, CancellationToken.None).ConfigureAwait(false);
                inFlight = null;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            int queuedTailCount;
            lock (writeGate)
            {
                if (inFlight is not null) retryQueue.Enqueue(inFlight);
                while (persistenceQueue.Reader.TryRead(out var queued)) retryQueue.Enqueue(queued);
                queuedTailCount = retryQueue.Count;
                var reportedFailure = queuedTailCount == 0
                    ? exception
                    : new StorageException($"Process lifecycle persistence stopped after '{exception.Message}'; {queuedTailCount} lifecycle fact(s) are retained for retry.", exception);
                Volatile.Write(ref persistenceFailure, reportedFailure);
                health?.RecordPipelineFailure(reportedFailure);
                persistenceQueue.Writer.TryComplete(reportedFailure);
            }
        }
    }

    private static Channel<ProcessLifecycleEvent> CreatePersistenceQueue() => Channel.CreateBounded<ProcessLifecycleEvent>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    private void UpdateExitCache(ProcessInstanceId processInstanceId, DateTimeOffset exitedUtc)
    {
        if (exitTimes.TryAdd(processInstanceId, exitedUtc)) insertionOrder.Enqueue(processInstanceId);
        else exitTimes[processInstanceId] = exitedUtc;
        while (exitTimes.Count > Capacity && insertionOrder.TryDequeue(out var oldest)) exitTimes.TryRemove(oldest, out _);
    }
}
