using System.Collections.Concurrent;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Abstractions;

namespace StorageChronicle.Agent;

/// <summary>Keeps bounded, process-instance exit times in memory for Activity Frame lifecycle projections.</summary>
public sealed class AgentProcessLifecycleState : IProcessLifecycleSink
{
    private const int Capacity = 8192;
    private readonly ConcurrentDictionary<ProcessInstanceId, DateTimeOffset> exitTimes = new();
    private readonly ConcurrentQueue<ProcessInstanceId> insertionOrder = new();

    /// <inheritdoc />
    public void RecordProcessExit(ProcessInstanceId processInstanceId, DateTimeOffset exitedUtc)
    {
        if (string.IsNullOrWhiteSpace(processInstanceId.Value)) throw new ArgumentException("A process instance identity is required.", nameof(processInstanceId));
        if (exitedUtc == default) throw new ArgumentException("A process exit time is required.", nameof(exitedUtc));
        if (exitTimes.TryAdd(processInstanceId, exitedUtc)) insertionOrder.Enqueue(processInstanceId);
        else exitTimes.AddOrUpdate(processInstanceId, exitedUtc, (_, existing) => exitedUtc < existing ? exitedUtc : existing);
        while (exitTimes.Count > Capacity && insertionOrder.TryDequeue(out var oldest)) exitTimes.TryRemove(oldest, out _);
    }

    /// <inheritdoc />
    public bool TryGetProcessExit(ProcessInstanceId processInstanceId, out DateTimeOffset exitedUtc) => exitTimes.TryGetValue(processInstanceId, out exitedUtc);
}
