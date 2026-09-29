using System.Collections.Immutable;
using System.Text.Json;
using StorageChronicle.Application;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Normalization;
using StorageChronicle.Storage;
using Xunit;

namespace StorageChronicle.EndToEnd.Tests;

/// <summary>Runs deterministic Golden fixtures through the real append log, SQLite index, and state boundary.</summary>
public sealed class GoldenFixtureTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] SqliteIndexFileNames = ["index.sqlite", "index.sqlite-wal", "index.sqlite-shm"];

    [Fact]
    public async Task CreationDeleteGoldenFixtureSurvivesAgentRestartAndSqliteRebuild()
    {
        var fixture = await LoadFixtureAsync("creation-delete.json", TestContext.Current.CancellationToken);
        var fixtureRoot = CreateFixtureRoot(out var runId);
        var directory = Path.Combine(fixtureRoot, "history");
        Directory.CreateDirectory(directory);
        try
        {
            await using (var first = new AppendOnlyStorageEngine(new StorageEngineOptions(directory) { FlushInterval = TimeSpan.FromMinutes(1) }))
            {
                await new AgentPipeline(first, first, new EventNormalizer()).RunAsync(new FixtureCollector(fixture.Events), TestContext.Current.CancellationToken);
                await first.StopAsync(TestContext.Current.CancellationToken);
            }

            foreach (var sqliteFile in SqliteIndexFileNames.Select(name => Path.Combine(directory, name)).Where(File.Exists))
            {
                Assert.True(IsOwnedFixture(fixtureRoot, runId), "Only the current marked test fixture may have its rebuildable SQLite index files removed.");
                File.Delete(sqliteFile);
            }

            await using var restarted = new AppendOnlyStorageEngine(new StorageEngineOptions(directory) { FlushInterval = TimeSpan.FromMinutes(1) });
            await restarted.RebuildSqliteAsync(TestContext.Current.CancellationToken);
            var source = await ToListAsync(restarted.ReadSourceAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            var canonical = await ToListAsync(restarted.ReadCanonicalAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

            Assert.Equal(fixture.Expected.SourceCount, source.Count);
            Assert.Equal(fixture.Expected.CanonicalCount, canonical.Count);
            Assert.Equal(CanonicalOperation.Delete, canonical[^1].Operation);
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot, runId);
        }
    }

    [Fact]
    public async Task CapacityFailureIsExplicitAndDoesNotSilentlyDropSourceQuality()
    {
        var fixtureRoot = CreateFixtureRoot(out var runId);
        var directory = Path.Combine(fixtureRoot, "history");
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new AppendOnlyStorageEngine(new StorageEngineOptions(directory) { MinimumFreeBytes = long.MaxValue, FlushInterval = TimeSpan.FromMinutes(1) });
            await Assert.ThrowsAsync<StorageCapacityException>(async () => await store.AppendSourceAsync(CreateSource(1), TestContext.Current.CancellationToken));
            Assert.Equal(RecordingState.CapacityStopped, store.Status.State);
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot, runId);
        }
    }

    private static string CreateFixtureRoot(out string runId)
    {
        runId = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "StorageChronicle.EndToEnd.Tests", runId);
        Directory.CreateDirectory(root);
        using var marker = new FileStream(Path.Combine(root, ".test-owner.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(marker, new { Schema = "StorageChronicle.TestFixtureOwner.v1", RunId = runId });
        return root;
    }

    private static bool IsOwnedFixture(string root, string runId)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!string.Equals(Path.GetDirectoryName(fullRoot), Path.Combine(Path.GetTempPath(), "StorageChronicle.EndToEnd.Tests"), StringComparison.OrdinalIgnoreCase) || Path.GetFileName(fullRoot) != runId)
            return false;
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(fullRoot, ".test-owner.json")));
        return marker.RootElement.GetProperty("Schema").GetString() == "StorageChronicle.TestFixtureOwner.v1" && marker.RootElement.GetProperty("RunId").GetString() == runId;
    }

    private static void DeleteFixtureRoot(string root, string runId)
    {
        if (!Directory.Exists(root)) return;
        if (!IsOwnedFixture(root, runId)) throw new IOException("The end-to-end fixture ownership marker does not match this run.");
        EnsureNoReparsePoints(root);
        Directory.Delete(Path.GetFullPath(root), recursive: true);
    }

    private static void EnsureNoReparsePoints(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException($"A reparse point was found in the owned end-to-end fixture: {entry}");
            if (Directory.Exists(entry)) EnsureNoReparsePoints(entry);
        }
    }

    private static SourceEvent CreateSource(long sequence)
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var volume = VolumeId.Create("golden-volume");
        var file = FileId.Create("golden-file");
        var metadata = new FileMetadata(volume, file, null, "golden.txt", FileKind.File, 1, 4096, now, now, now, now, FileAttributes.Normal, null, null, EventQuality.Exact, true, false);
        return new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.LiveUsn, volume, file, null, metadata.Name, null, CanonicalOperation.Create, metadata, new EventTime(now, TimeSpan.Zero, null, now, new SourceSequence(sequence), new MountSequence(sequence)), EventQuality.Exact, null, ProcessAttributionQuality.Unknown, MountSessionId.Create("golden-mount"), null, ImmutableDictionary<string, string>.Empty);
    }

    private static async Task<GoldenFixture> LoadFixtureAsync(string fileName, CancellationToken cancellationToken)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Golden", fileName);
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<GoldenFixture>(stream, JsonOptions, cancellationToken) ?? throw new InvalidDataException(path);
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source, CancellationToken cancellationToken)
    {
        var values = new List<T>();
        await foreach (var value in source.WithCancellation(cancellationToken)) values.Add(value);
        return values;
    }

    private sealed record GoldenFixture(string Id, IReadOnlyList<GoldenFixtureEvent> Events, GoldenExpected Expected);
    private sealed record GoldenFixtureEvent(long Sequence, string Operation, string Name, string Quality);
    private sealed record GoldenExpected(int SourceCount, int CanonicalCount);

    private sealed class FixtureCollector(IReadOnlyList<GoldenFixtureEvent> fixture) : ISourceEventCollector
    {
        public async IAsyncEnumerable<SourceEvent> CollectAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var item in fixture)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = CreateSource(item.Sequence) with
                {
                    Hint = Enum.Parse<CanonicalOperation>(item.Operation, ignoreCase: false),
                    Name = item.Name,
                    Quality = Enum.Parse<EventQuality>(item.Quality, ignoreCase: false)
                };
                yield return source;
                await Task.Yield();
            }
        }
    }
}
