using System.Collections.Immutable;
using StorageChronicle.Agent;
using StorageChronicle.Domain.Contracts;
using Xunit;

namespace StorageChronicle.Agent.Tests;

public sealed class ReconciliationLiveEventBufferTests
{
    [Fact]
    public void CapturesOnlyPostBoundaryNonReconciliationEventsInSourceOrder()
    {
        var volume = VolumeId.Create("buffer-volume");
        var otherVolume = VolumeId.Create("other-volume");
        var buffer = new ReconciliationLiveEventBuffer(8);
        using var session = buffer.Begin(volume, 2);
        var later = Source(volume, 5);
        var earlier = Source(volume, 3);

        buffer.Observe(Source(volume, 2));
        buffer.Observe(new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.DirectoryReconciliation, volume, null, null, null, null, CanonicalOperation.ReconciliationDiscovered, null, Time(4), EventQuality.Reconciled, null, ProcessAttributionQuality.Unknown, null, null, ImmutableDictionary<string, string>.Empty.Add("reconciliationRunId", "scan")));
        buffer.Observe(Source(otherVolume, 6));
        buffer.Observe(later);
        buffer.Observe(earlier);
        buffer.Observe(later);

        var batch = session.Complete();

        Assert.False(batch.Overflowed);
        Assert.Equal([earlier.EventId, later.EventId], batch.Events.Select(value => value.EventId));
    }

    [Fact]
    public void OverflowIsReportedWithoutPretendingTheCaptureIsComplete()
    {
        var volume = VolumeId.Create("overflow-volume");
        var buffer = new ReconciliationLiveEventBuffer(1);
        using var session = buffer.Begin(volume, 0);

        buffer.Observe(Source(volume, 1));
        buffer.Observe(Source(volume, 2));

        var batch = session.Complete();

        Assert.True(batch.Overflowed);
        Assert.Single(batch.Events);
    }

    [Fact]
    public void SessionCanBeReopenedAfterCompletion()
    {
        var volume = VolumeId.Create("reopen-volume");
        var buffer = new ReconciliationLiveEventBuffer();
        using (var first = buffer.Begin(volume, 0))
        {
            Assert.Empty(first.Complete().Events);
        }

        using var second = buffer.Begin(volume, 1);
        Assert.Equal(1, second.SourceSequenceBoundary);
    }

    private static SourceEvent Source(VolumeId volume, long sequence) =>
        new(EventId.New(), EventSchemaVersion.Current, EventOrigin.LiveUsn, volume, null, null, $"file-{sequence}", null, CanonicalOperation.Create, null, Time(sequence), EventQuality.Exact, null, ProcessAttributionQuality.Unknown, null, null, ImmutableDictionary<string, string>.Empty);

    private static EventTime Time(long sequence)
    {
        var now = DateTimeOffset.UtcNow;
        return new EventTime(now, TimeSpan.Zero, null, now, new SourceSequence(sequence), new MountSequence(sequence));
    }
}
