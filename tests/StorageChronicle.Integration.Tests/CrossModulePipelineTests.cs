using System.Collections.Immutable;
using StorageChronicle.Application;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Normalization;
using StorageChronicle.Storage;
using Xunit;

namespace StorageChronicle.Integration.Tests;

public sealed class CrossModulePipelineTests
{
    [Fact]
    public async Task CollectorToAppendLogAndStateIsRecoverable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "StorageChronicle.Integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new AppendOnlyStorageEngine(new StorageEngineOptions(directory) { FlushInterval = TimeSpan.FromMinutes(1) });
            await new AgentPipeline(store, store, new EventNormalizer()).RunAsync(new OneEventCollector(CreateSource()));
            await store.StopAsync();
            Assert.Single(await ToListAsync(store.ReadSourceAsync()));
            Assert.Single(await ToListAsync(store.ReadCanonicalAsync()));
            Assert.Single((await store.GetSnapshotAsync(DateTimeOffset.UtcNow)).Entries);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static SourceEvent CreateSource()
    {
        var volume = VolumeId.Create("volume-integration");
        var file = FileId.Create("file-integration");
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var metadata = new FileMetadata(volume, file, null, "document.txt", FileKind.File, 1, 4096, now, now, now, now, FileAttributes.Normal, null, null, EventQuality.Exact, true, false);
        return new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.LiveUsn, volume, file, null, metadata.Name, null, CanonicalOperation.Create, metadata, new EventTime(now, TimeSpan.Zero, null, now, new SourceSequence(1), new MountSequence(1)), EventQuality.Exact, null, ProcessAttributionQuality.Unknown, MountSessionId.Create("mount-integration"), null, ImmutableDictionary<string, string>.Empty);
    }

    private sealed class OneEventCollector(SourceEvent value) : ISourceEventCollector
    {
        public async IAsyncEnumerable<SourceEvent> CollectAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
            await Task.CompletedTask;
        }
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> values)
    {
        var result = new List<T>();
        await foreach (var value in values) result.Add(value);
        return result;
    }
}
