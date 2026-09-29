using System.Collections.Immutable;
using StorageChronicle.Application;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Normalization;
using Xunit;

namespace StorageChronicle.Agent.Tests;

public sealed class AgentPipelineTests
{
    [Fact]
    public async Task PipelineStoresSourceThenCanonicalAndAppliesState()
    {
        var store = new FakeStore();
        var state = new FakeState();
        var source = Source(1);
        var committed = new List<SourceEvent>();
        var pipeline = new AgentPipeline(store, state, new EventNormalizer(), 1);
        pipeline.SourceCommitted += committed.Add;
        await pipeline.RunAsync(new FakeCollector(source));
        Assert.Single(store.Sources);
        Assert.Single(store.Canonicals);
        Assert.Single(state.Values);
        Assert.Equal(source.EventId, store.Sources[0].EventId);
        Assert.Equal([source.EventId], committed.Select(value => value.EventId));
    }

    [Fact]
    public async Task BoundedQueueReportsDepthAndReturnsToZeroAfterDrain()
    {
        var depths = new System.Collections.Concurrent.ConcurrentBag<int>();
        var pipeline = new AgentPipeline(new FakeStore(), new FakeState(), new EventNormalizer(), 1);
        pipeline.QueueDepthChanged += depths.Add;

        await pipeline.RunAsync(new FakeCollector(Source(11)));

        Assert.Contains(1, depths);
        Assert.Contains(0, depths);
    }

    [Fact]
    public async Task CancellationDoesNotLeaveAnUnboundedProducer()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AgentPipeline(new FakeStore(), new FakeState(), new EventNormalizer()).RunAsync(new FakeCollector(Source(1)), cts.Token));
    }

    [Fact]
    public async Task OneCollectorFailureIsReportedWithoutDroppingAnotherCollector()
    {
        var store = new FakeStore();
        var state = new FakeState();
        var pipeline = new AgentPipeline(store, state, new EventNormalizer());
        var failures = 0;
        var observed = 0;
        pipeline.CollectorFailed += (_, _) => failures++;
        pipeline.SourceObserved += _ => observed++;
        await pipeline.RunAsync(new ISourceEventCollector[] { new ThrowingCollector(), new FakeCollector(Source(2)) });
        Assert.Equal(1, failures);
        Assert.Equal(1, observed);
        Assert.Single(store.Sources);
        Assert.Single(state.Values);
    }

    [Fact]
    public async Task DuplicateSourceEventIdIsPersistedOnlyOnceWithinTheBoundedWindow()
    {
        var store = new FakeStore();
        var state = new FakeState();
        var source = Source(3);
        await new AgentPipeline(store, state, new EventNormalizer(), 2).RunAsync(new FakeCollector(source, source));

        Assert.Single(store.Sources);
        Assert.Single(store.Canonicals);
        Assert.Single(state.Values);
    }

    private static SourceEvent Source(long sequence) { var now = DateTimeOffset.UtcNow; return new(EventId.New(), EventSchemaVersion.Current, EventOrigin.LiveUsn, VolumeId.Create("v"), FileId.Create("f"), null, "f", null, CanonicalOperation.Create, null, new EventTime(now, TimeSpan.Zero, null, now, new SourceSequence(sequence), new MountSequence(sequence)), EventQuality.Exact, null, ProcessAttributionQuality.Unknown, null, null, ImmutableDictionary<string, string>.Empty); }

    private sealed class FakeCollector(params SourceEvent[] values) : ISourceEventCollector
    {
        public async IAsyncEnumerable<SourceEvent> CollectAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { foreach (var value in values) { cancellationToken.ThrowIfCancellationRequested(); yield return value; await Task.Yield(); } }
    }

    private sealed class ThrowingCollector : ISourceEventCollector
    {
        public async IAsyncEnumerable<SourceEvent> CollectAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (cancellationToken.IsCancellationRequested) yield break;
            throw new IOException("collector failure");
        }
    }

    private sealed class FakeStore : IEventStore
    {
        public List<SourceEvent> Sources { get; } = [];
        public List<CanonicalEvent> Canonicals { get; } = [];
        public ValueTask AppendSourceAsync(SourceEvent value, CancellationToken cancellationToken = default) { Sources.Add(value); return ValueTask.CompletedTask; }
        public ValueTask AppendCanonicalAsync(CanonicalEvent value, CancellationToken cancellationToken = default) { Canonicals.Add(value); return ValueTask.CompletedTask; }
        public async IAsyncEnumerable<CanonicalEvent> ReadCanonicalAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { foreach (var value in Canonicals) { cancellationToken.ThrowIfCancellationRequested(); yield return value; await Task.Yield(); } }
    }

    private sealed class FakeState : IStateStore
    {
        public List<CanonicalEvent> Values { get; } = [];
        public ValueTask ApplyAsync(CanonicalEvent value, CancellationToken cancellationToken = default) { Values.Add(value); return ValueTask.CompletedTask; }
        public ValueTask<FileStateSnapshot> GetSnapshotAsync(DateTimeOffset atUtc, CancellationToken cancellationToken = default) => ValueTask.FromResult(new FileStateSnapshot(atUtc, Array.Empty<FileStateEntry>(), Array.Empty<FileStateEntry>()));
    }
}
