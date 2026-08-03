using System.Collections.Concurrent;
using StorageChronicle.Application;
using StorageChronicle.Contracts;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Storage;

namespace StorageChronicle.Agent;

/// <summary>Aggregates durable recording and per-volume continuity facts for health IPC.</summary>
public sealed class AgentHealthState
{
    private const int PendingCapacity = 256;
    private readonly ConcurrentDictionary<string, VolumeHealth> volumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingReconciliationRequest> pending = new(StringComparer.Ordinal);
    private string? failureReason;
    private int queueDepth;

    /// <summary>Observes one source fact without retaining its path or contents.</summary>
    public void Observe(SourceEvent source)
        => Observe(source, createPendingReconciliation: true);

    /// <summary>Observes a source fact and optionally suppresses a second prompt for a user-declined gap.</summary>
    public void Observe(SourceEvent source, bool createPendingReconciliation)
    {
        var isGap = source.Quality == EventQuality.UnverifiedGap || source.Hint == CanonicalOperation.UnverifiedGap;
        var continuity = isGap
            ? MonitoringContinuity.UnverifiedGap
            : source.Quality == EventQuality.Reconciled || source.Origin is EventOrigin.MftReconciliation or EventOrigin.DirectoryReconciliation
                ? MonitoringContinuity.ReconciledState
                : source.Origin == EventOrigin.RecoveredUsn ? MonitoringContinuity.JournalRecovered : MonitoringContinuity.Continuous;
        var reason = source.Properties.GetValueOrDefault("reconciliationReason") ?? source.Properties.GetValueOrDefault("reason");
        if (source.VolumeId is { } volume)
        {
            if (!isGap && volumes.TryGetValue(volume.Value, out var previous) && previous.Continuity == MonitoringContinuity.UnverifiedGap &&
                source.Origin is not (EventOrigin.MftReconciliation or EventOrigin.DirectoryReconciliation or EventOrigin.RecoveredUsn))
            {
                continuity = MonitoringContinuity.UnverifiedGap;
                reason ??= previous.GapReason;
            }

            volumes[volume.Value] = new VolumeHealth(volume, continuity, reason);
            if (isGap && createPendingReconciliation && pending.Count < PendingCapacity && !pending.Values.Any(value => value.VolumeId == volume))
            {
                var requestId = source.EventId.ToString();
                pending.TryAdd(requestId, new PendingReconciliationRequest(
                    requestId,
                    volume,
                    string.IsNullOrWhiteSpace(reason) ? "Monitoring continuity was not confirmed." : reason,
                    source.Time.SourceSequence.Value,
                    source.Time.RecordedUtc));
            }
        }
        else if (isGap && createPendingReconciliation && pending.Count < PendingCapacity)
        {
            var requestId = source.EventId.ToString();
            pending.TryAdd(requestId, new PendingReconciliationRequest(
                requestId,
                null,
                string.IsNullOrWhiteSpace(reason) ? "Monitoring continuity was not confirmed." : reason,
                source.Time.SourceSequence.Value,
                source.Time.RecordedUtc));
        }
    }

    /// <summary>Records an isolated collector failure for the health surface.</summary>
    public void RecordCollectorFailure(ISourceEventCollector collector, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(exception);
        Volatile.Write(ref failureReason, $"{collector.GetType().Name}: {exception.Message}");
    }

    /// <summary>Records a secondary durable sink failure without stopping primary capture.</summary>
    public void RecordSinkFailure(ICanonicalEventSink sink, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(exception);
        Volatile.Write(ref failureReason, $"{sink.GetType().Name}: {exception.Message}");
    }

    /// <summary>Records a pipeline/storage failure without terminating the Agent supervisor.</summary>
    public void RecordPipelineFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Volatile.Write(ref failureReason, $"Pipeline: {exception.Message}");
    }

    /// <summary>Clears a previous isolated failure after a supervised monitoring restart.</summary>
    public void ClearFailure() => Volatile.Write(ref failureReason, null);

    /// <summary>Publishes the current bounded pipeline queue depth without retaining event data.</summary>
    public void SetQueueDepth(int value) => Volatile.Write(ref queueDepth, Math.Max(0, value));

    /// <summary>Returns a UI-safe health snapshot.</summary>
    public AgentHealth Snapshot(RecordingStatus status, bool markPendingPresented = false)
    {
        var currentFailure = Volatile.Read(ref failureReason);
        var state = status.State == RecordingState.Running && currentFailure is null ? "Running" : status.State.ToString();
        var requests = pending.Values.OrderBy(value => value.DiscoveredUtc).ThenBy(value => value.RequestId, StringComparer.Ordinal).ToArray();
        if (markPendingPresented)
        {
            foreach (var request in requests.Where(value => !value.Presented))
            {
                pending.TryUpdate(request.RequestId, request with { Presented = true }, request);
            }
        }

        return new AgentHealth(state, currentFailure ?? status.Reason, status.LastSequence,
            volumes.Values.OrderBy(value => value.VolumeId.Value, StringComparer.OrdinalIgnoreCase).ToArray(), requests, Volatile.Read(ref queueDepth));
    }

    /// <summary>Resolves one pending request after the UI has made its decision.</summary>
    public bool TryResolve(string requestId) => !string.IsNullOrWhiteSpace(requestId) && pending.TryRemove(requestId, out _);

    /// <summary>Looks up a pending request without exposing the mutable health dictionary.</summary>
    public bool TryGetPending(string requestId, out PendingReconciliationRequest request)
        => pending.TryGetValue(requestId, out request!);
}
