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

    [Fact]
    public async Task CreationDeleteGoldenFixtureSurvivesAgentRestartAndSqliteRebuild()
    {
        var fixture = await LoadFixtureAsync("creation-delete.json", TestContext.Current.CancellationToken);
        var directory = Path.Combine(Path.GetTempPath(), "StorageChronicle.Golden", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using (var first = new AppendOnlyStorageEngine(new StorageEngineOptions(directory) { FlushInterval = TimeSpan.FromMinutes(1) }))
            {
                await new AgentPipeline(first, first, new EventNormalizer()).RunAsync(new FixtureCollector(fixture.Events), TestContext.Current.CancellationToken);
                await first.StopAsync(TestContext.Current.CancellationToken);
            }

            foreach (var sqliteFile in Directory.EnumerateFiles(directory, "*.db*")) File.Delete(sqliteFile);

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
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CapacityFailureIsExplicitAndDoesNotSilentlyDropSourceQuality()
    {
        var directory = Path.Combine(Path.GetTempPath(), "StorageChronicle.Capacity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new AppendOnlyStorageEngine(new StorageEngineOptions(directory) { MinimumFreeBytes = long.MaxValue, FlushInterval = TimeSpan.FromMinutes(1) });
            await Assert.ThrowsAsync<StorageCapacityException>(async () => await store.AppendSourceAsync(CreateSource(1), TestContext.Current.CancellationToken));
            Assert.Equal(RecordingState.CapacityStopped, store.Status.State);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
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
