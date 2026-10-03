using System.Collections.Immutable;
using StorageChronicle.Contracts;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Storage;
using Xunit;

namespace StorageChronicle.Agent.Tests;

public sealed class AgentHealthStateTests
{
    [Fact]
    public void MediaApprovalRemainsPendingUntilExplicitResolutionAndPreservesUnknownDisclosure()
    {
        var state = new AgentHealthState();
        var request = new PendingMediaMirrorApproval(
            "request-1", "pc-a", "media-a", "volume-a", "X:\\.StorageChronicle", "exFAT",
            MediaMirrorAclDisclosure.Unknown, DateTimeOffset.UtcNow);

        Assert.True(state.TryAddPendingMediaApproval(request));
        var snapshot = state.Snapshot(new RecordingStatus(RecordingState.Running, 1, 1, null));
        Assert.Equal(request, Assert.Single(snapshot.PendingMediaMirrorApprovals!));
        Assert.True(state.TryGetPendingMediaApproval(request.RequestId, out var pending));
        Assert.Equal(MediaMirrorAclDisclosure.Unknown, pending.AclDisclosure);
        Assert.True(state.TryResolveMediaApproval(request.RequestId));
        Assert.Empty(state.Snapshot(new RecordingStatus(RecordingState.Running, 1, 1, null)).PendingMediaMirrorApprovals!);
    }

    [Fact]
    public void MediaApprovalQueueRejectsIncompleteIdentity()
    {
        var state = new AgentHealthState();
        var request = new PendingMediaMirrorApproval(
            "request-2", "", "media-a", "volume-a", "X:\\.StorageChronicle", "NTFS",
            MediaMirrorAclDisclosure.NtfsAclVerified, DateTimeOffset.UtcNow);

        Assert.False(state.TryAddPendingMediaApproval(request));
        Assert.Empty(state.Snapshot(new RecordingStatus(RecordingState.Running, 1, 1, null)).PendingMediaMirrorApprovals!);
    }

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

    [Fact]
    public async Task RestartRehydratesAnUnresolvedGapFromCanonicalHistory()
    {
        var fixtureRoot = AgentTestFixtureOwnership.CreateTempRoot("StorageChronicle.AgentHealth", out var runId);
        var root = Path.Combine(fixtureRoot, "history");
        try
        {
            var volume = VolumeId.Create("\\\\?\\Volume{health-history}\\");
            var now = DateTimeOffset.UtcNow;
            var source = new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.LiveUsn, volume, null, null, null, null,
                CanonicalOperation.UnverifiedGap, null,
                new EventTime(now, now.Offset, null, now, new SourceSequence(11), new MountSequence(11)),
                EventQuality.UnverifiedGap, null, ProcessAttributionQuality.Unknown, null, null,
                ImmutableDictionary<string, string>.Empty.Add("reconciliationReason", "journal gap"));
            var canonical = new StorageChronicle.Normalization.EventNormalizer().Normalize(source)!;
            await using (var storage = new AppendOnlyStorageEngine(new StorageEngineOptions(root) { FlushInterval = TimeSpan.FromMinutes(1) }))
            {
                await storage.AppendCanonicalAsync(canonical);
                await storage.ApplyAsync(canonical);
                var state = new AgentHealthState();

                await state.RestoreFromHistoryAsync(storage);

                var request = Assert.Single(state.Snapshot(new RecordingStatus(RecordingState.Running, 1, 11, null)).PendingReconciliations!);
                Assert.Equal(volume, request.VolumeId);
                Assert.Equal("journal gap", request.Reason);
            }
        }
        finally
        {
            AgentTestFixtureOwnership.DeleteTempRoot(fixtureRoot, "StorageChronicle.AgentHealth", runId);
        }
    }

    [Fact]
    public async Task RestartDoesNotRecreateAUserDeclinedGap()
    {
        var fixtureRoot = AgentTestFixtureOwnership.CreateTempRoot("StorageChronicle.AgentHealth", out var runId);
        var root = Path.Combine(fixtureRoot, "history");
        try
        {
            var volume = VolumeId.Create("\\\\?\\Volume{health-declined}\\");
            var now = DateTimeOffset.UtcNow;
            var source = new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.DirectoryReconciliation, volume, null, null, null, null,
                CanonicalOperation.UnverifiedGap, null,
                new EventTime(now, now.Offset, null, now, new SourceSequence(12), new MountSequence(12)),
                EventQuality.UnverifiedGap, null, ProcessAttributionQuality.Unknown, null, null,
                ImmutableDictionary<string, string>.Empty.Add("reconciliationReason", "declined").Add("userDeclined", "true"));
            var canonical = new StorageChronicle.Normalization.EventNormalizer().Normalize(source)!;
            await using (var storage = new AppendOnlyStorageEngine(new StorageEngineOptions(root) { FlushInterval = TimeSpan.FromMinutes(1) }))
            {
                await storage.AppendCanonicalAsync(canonical);
                await storage.ApplyAsync(canonical);
                var state = new AgentHealthState();

                await state.RestoreFromHistoryAsync(storage);

                Assert.Empty(state.Snapshot(new RecordingStatus(RecordingState.Running, 1, 12, null)).PendingReconciliations!);
            }
        }
        finally
        {
            AgentTestFixtureOwnership.DeleteTempRoot(fixtureRoot, "StorageChronicle.AgentHealth", runId);
        }
    }
}
