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
        var runId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), "StorageChronicle.Integration", runId);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, ".test-owner"), runId, TestContext.Current.CancellationToken);
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
            CleanupFixture(directory);
        }
    }

    private static void CleanupFixture(string directory)
    {
        if (!Directory.Exists(directory)) return;
        var owner = Path.Combine(directory, ".test-owner");
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), Path.GetFullPath(Path.Combine(Path.GetTempPath(), "StorageChronicle.Integration")), StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(directory), "N", out _) || !File.Exists(owner) || !string.Equals(File.ReadAllText(owner), Path.GetFileName(directory), StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to clean an integration fixture without its run owner marker.");
        var boundary = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            var resolved = Path.GetFullPath(entry);
            if (!resolved.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing to clean an integration fixture with an out-of-root path or reparse point.");
        }
        Directory.Delete(directory, recursive: true);
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
