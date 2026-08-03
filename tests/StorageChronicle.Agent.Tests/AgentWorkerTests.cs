using System.Collections.Immutable;
using StorageChronicle.Application;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Normalization;
using StorageChronicle.Storage;
using Xunit;

namespace StorageChronicle.Agent.Tests;

public sealed class AgentWorkerTests
{
    [Fact]
    public async Task PipelineStorageFailureIsSupervisedAndDoesNotFaultHostedWorker()
    {
        var root = Path.Combine(Path.GetTempPath(), "storage-chronicle-agent-worker-" + Guid.NewGuid().ToString("N"));
        var storage = new AppendOnlyStorageEngine(new StorageEngineOptions(root)
        {
            FlushInterval = TimeSpan.FromHours(1),
            CompressClosedSegments = false
        });
        var health = new AgentHealthState();
        var worker = new AgentWorker(
            new ThrowingEventStore(),
            new NoopStateStore(),
            new EventNormalizer(),
            new[] { new SingleSourceCollector() },
            storage,
            health);

        try
        {
            await worker.StartAsync(CancellationToken.None);
            var observed = await WaitForAsync(
                () => health.Snapshot(storage.Status).Reason?.Contains("Pipeline:", StringComparison.Ordinal) == true,
                TimeSpan.FromSeconds(3));

            Assert.True(observed, "The supervised pipeline failure was not published to Agent health.");
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await worker.StopAsync(stopTimeout.Token);
        }
        finally
        {
            await storage.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(20);
        }

        return predicate();
    }

    private sealed class ThrowingEventStore : IEventStore
    {
        public ValueTask AppendSourceAsync(SourceEvent value, CancellationToken cancellationToken = default)
            => ValueTask.FromException(new IOException("simulated SQLite lock"));

        public ValueTask AppendCanonicalAsync(CanonicalEvent value, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public async IAsyncEnumerable<CanonicalEvent> ReadCanonicalAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class NoopStateStore : IStateStore
    {
        public ValueTask ApplyAsync(CanonicalEvent value, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<FileStateSnapshot> GetSnapshotAsync(DateTimeOffset atUtc, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new FileStateSnapshot(atUtc, Array.Empty<FileStateEntry>(), Array.Empty<FileStateEntry>()));
    }

    private sealed class SingleSourceCollector : ISourceEventCollector
    {
        public async IAsyncEnumerable<SourceEvent> CollectAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            yield return new SourceEvent(
                EventId.New(), EventSchemaVersion.Current, EventOrigin.LiveUsn, VolumeId.Create("worker-test-volume"),
                FileId.Create("worker-test-file"), null, "worker-test-file", null, CanonicalOperation.Create, null,
                new EventTime(now, TimeSpan.Zero, null, now, new SourceSequence(1), new MountSequence(1)),
                EventQuality.Exact, null, ProcessAttributionQuality.Unknown, null, null,
                ImmutableDictionary<string, string>.Empty);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
