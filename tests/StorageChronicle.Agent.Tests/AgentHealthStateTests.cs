using System.Collections.Immutable;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Storage;
using Xunit;

namespace StorageChronicle.Agent.Tests;

public sealed class AgentHealthStateTests
{
    [Fact]
    public void GapIsExposedOnceThenRetainedAsPresentedUntilDecision()
    {
        var state = new AgentHealthState();
        var now = DateTimeOffset.UtcNow;
        var source = new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.Etw, null, null, null, null, null,
            CanonicalOperation.UnverifiedGap, null,
            new EventTime(now, now.Offset, null, now, new SourceSequence(7), new MountSequence(7)),
            EventQuality.UnverifiedGap, null, ProcessAttributionQuality.Unknown, null, null,
            ImmutableDictionary<string, string>.Empty.Add("reconciliationReason", "ETW overflow"));

        state.Observe(source);
        var first = state.Snapshot(new RecordingStatus(RecordingState.Running, 1, 7, null), markPendingPresented: true);
        var second = state.Snapshot(new RecordingStatus(RecordingState.Running, 1, 7, null));

        var firstRequest = Assert.Single(first.PendingReconciliations!);
        var secondRequest = Assert.Single(second.PendingReconciliations!);
        Assert.False(firstRequest.Presented);
        Assert.True(secondRequest.Presented);
        Assert.True(state.TryResolve(firstRequest.RequestId));
        Assert.Empty(state.Snapshot(new RecordingStatus(RecordingState.Running, 1, 7, null)).PendingReconciliations!);
    }

    [Fact]
    public void ReconciliationChangesContinuityToReconciledState()
    {
        var state = new AgentHealthState();
        var volume = VolumeId.Create("\\\\?\\Volume{test}\\");
        var now = DateTimeOffset.UtcNow;
        state.Observe(new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.MftReconciliation, volume, null, null, null, null,
            CanonicalOperation.ReconciliationDiscovered, null,
            new EventTime(now, now.Offset, null, now, new SourceSequence(1), new MountSequence(1)),
            EventQuality.Reconciled, null, ProcessAttributionQuality.Unknown, null, null, ImmutableDictionary<string, string>.Empty));

        Assert.Equal(MonitoringContinuity.ReconciledState, Assert.Single(state.Snapshot(new RecordingStatus(RecordingState.Running, 1, 1, null)).Volumes).Continuity);
    }

    [Fact]
    public void PipelineFailureIsExposedAsQualityStateWithoutChangingRecordingStatus()
    {
        var state = new AgentHealthState();

        state.RecordPipelineFailure(new IOException("SQLite is temporarily locked."));

        var snapshot = state.Snapshot(new RecordingStatus(RecordingState.Running, 3, 3, null));
        Assert.Equal("Running", snapshot.State);
        Assert.Contains("Pipeline:", snapshot.Reason, StringComparison.Ordinal);
        Assert.Contains("temporarily locked", snapshot.Reason, StringComparison.Ordinal);
    }
}
