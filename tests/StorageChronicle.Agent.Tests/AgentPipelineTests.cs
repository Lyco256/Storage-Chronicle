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
        await new AgentPipeline(store, state, new EventNormalizer(), 1).RunAsync(new FakeCollector(source));
        Assert.Single(store.Sources);
        Assert.Single(store.Canonicals);
        Assert.Single(state.Values);
        Assert.Equal(source.EventId, store.Sources[0].EventId);
    }

    [Fact]
    public async Task CancellationDoesNotLeaveAnUnboundedProducer()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AgentPipeline(new FakeStore(), new FakeState(), new EventNormalizer()).RunAsync(new FakeCollector(Source(1)), cts.Token));
    }

    private static SourceEvent Source(long sequence) { var now = DateTimeOffset.UtcNow; return new(EventId.New(), EventSchemaVersion.Current, EventOrigin.LiveUsn, VolumeId.Create("v"), FileId.Create("f"), null, "f", null, CanonicalOperation.Create, null, new EventTime(now, TimeSpan.Zero, null, now, new SourceSequence(sequence), new MountSequence(sequence)), EventQuality.Exact, null, ProcessAttributionQuality.Unknown, null, null, ImmutableDictionary<string, string>.Empty); }

    private sealed class FakeCollector(params SourceEvent[] values) : ISourceEventCollector
    {
        public async IAsyncEnumerable<SourceEvent> CollectAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { foreach (var value in values) { cancellationToken.ThrowIfCancellationRequested(); yield return value; await Task.Yield(); } }
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
