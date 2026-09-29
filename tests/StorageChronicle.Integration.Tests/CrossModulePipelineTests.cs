using System.Collections.Immutable;
using System.Text.Json;
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
        var runId = Guid.NewGuid().ToString("N");
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "StorageChronicle.Integration", runId);
        Directory.CreateDirectory(fixtureRoot);
        using (var marker = new FileStream(Path.Combine(fixtureRoot, ".test-owner.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            JsonSerializer.Serialize(marker, new { Schema = "StorageChronicle.TestFixtureOwner.v1", RunId = runId });
        var directory = Path.Combine(fixtureRoot, "history");
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
            if (Directory.Exists(fixtureRoot))
            {
                using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureRoot, ".test-owner.json")));
                Assert.Equal("StorageChronicle.TestFixtureOwner.v1", marker.RootElement.GetProperty("Schema").GetString());
                Assert.Equal(runId, marker.RootElement.GetProperty("RunId").GetString());
                Assert.Equal(runId, Path.GetFileName(fixtureRoot));
                Assert.Equal(Path.Combine(Path.GetTempPath(), "StorageChronicle.Integration"), Path.GetDirectoryName(fixtureRoot));
                EnsureNoReparsePoints(fixtureRoot);
                Directory.Delete(fixtureRoot, recursive: true);
            }
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

    private static void EnsureNoReparsePoints(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException($"A reparse point was found in the owned integration fixture: {entry}");
            if (Directory.Exists(entry)) EnsureNoReparsePoints(entry);
        }
    }
}
