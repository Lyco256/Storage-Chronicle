using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Storage;
using Xunit;

namespace StorageChronicle.Storage.Tests;

public sealed class StorageEngineTests
{
    [Fact]
    public async Task RoundTripUsesClosedCompressedSegmentsAndRealSqliteState()
    {
        var directory = CreateDirectory();
        try
        {
            await using (var store = CreateStore(directory))
            {
                var source = CreateSource(1);
                await store.AppendSourceAsync(source);
                var canonical = CreateCanonical(source);
                await store.AppendCanonicalAsync(canonical);
                await store.ApplyAsync(canonical);
                await store.StopAsync();

                var sourceEvents = await ToListAsync(store.ReadSourceAsync());
                var canonicalEvents = await ToListAsync(store.ReadCanonicalAsync());
                Assert.Single(sourceEvents);
                Assert.Single(canonicalEvents);
                Assert.Equal(RecordingState.Completed, store.Status.State);
                var snapshot = await store.GetSnapshotAsync(DateTimeOffset.UtcNow);
                Assert.Single(snapshot.Entries);
            }

            var segments = Directory.GetFiles(directory, "*.zst");
            Assert.NotEmpty(segments);
            Assert.Empty(Directory.GetFiles(directory, "*.open"));
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "index.sqlite") }.ToString()))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('event_index', 'current_state', 'path_search', 'process', 'volume', 'mount_session', 'projection_cache') ORDER BY name;";
                using var reader = await command.ExecuteReaderAsync();
                var tables = new List<string>();
                while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
                Assert.Equal(7, tables.Count);
            }
        }
        finally
        {
            RemoveDirectory(directory);
        }
    }

    [Fact]
    public async Task DeletedSqliteIsRebuiltFromSegments()
    {
        var directory = CreateDirectory();
        try
        {
            await using (var store = CreateStore(directory))
            {
                var source = CreateSource(1);
                await store.AppendSourceAsync(source);
                await store.AppendCanonicalAsync(CreateCanonical(source));
                await store.StopAsync();
            }

            File.Delete(Path.Combine(directory, "index.sqlite"));
            File.Delete(Path.Combine(directory, "index.sqlite-wal"));
            File.Delete(Path.Combine(directory, "index.sqlite-shm"));
            await using (var recovered = CreateStore(directory))
            {
                await recovered.RebuildSqliteAsync();
                Assert.Single(await ToListAsync(recovered.ReadSourceAsync()));
                var snapshot = await recovered.GetSnapshotAsync(DateTimeOffset.UtcNow);
                Assert.Single(snapshot.Entries);
            }
        }
        finally
        {
            RemoveDirectory(directory);
        }
    }

    [Fact]
    public async Task CorruptClosedSegmentIsSkippedWithoutBlockingHealthySegments()
    {
        var directory = CreateDirectory();
        try
        {
            await using (var store = CreateStore(directory, segmentMaxBytes: 1024))
            {
                for (var i = 1; i <= 12; i++) await store.AppendSourceAsync(CreateSource(i));
                await store.StopAsync();
            }

            var segments = Directory.GetFiles(directory, "*.zst");
            Assert.True(segments.Length > 1);
            var damaged = segments[segments.Length / 2];
            var bytes = await File.ReadAllBytesAsync(damaged);
            bytes[^1] ^= 0x5a;
            await File.WriteAllBytesAsync(damaged, bytes);

            await using (var recovered = CreateStore(directory, segmentMaxBytes: 1024))
            {
                var healthy = await recovered.EnumerateHealthySegmentsAsync();
                Assert.Equal(segments.Length - 1, healthy.Count);
                Assert.NotEmpty(await ToListAsync(recovered.ReadSourceAsync()));
            }
        }
        finally
        {
            RemoveDirectory(directory);
        }
    }

    [Fact]
    public async Task CapacityFailureStopsRecordingAndDoesNotDeleteHistory()
    {
        var directory = CreateDirectory();
        try
        {
            await using var store = new AppendOnlyStorageEngine(new StorageEngineOptions(directory)
            {
                MinimumFreeBytes = 1,
                CapacityProbe = new FixedCapacityProbe(0)
            });
            await Assert.ThrowsAsync<StorageCapacityException>(async () => await store.AppendSourceAsync(CreateSource(1)));
            Assert.Equal(RecordingState.CapacityStopped, store.Status.State);
            Assert.Empty(Directory.GetFiles(directory, "segment-*"));
        }
        finally
        {
            RemoveDirectory(directory);
        }
    }

    [Fact]
    public async Task CancellationBeforeAppendLeavesNoRecord()
    {
        var directory = CreateDirectory();
        try
        {
            await using var store = CreateStore(directory);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.AppendSourceAsync(CreateSource(1), cancellation.Token));
            Assert.Empty(await ToListAsync(store.ReadSourceAsync()));
        }
        finally
        {
            RemoveDirectory(directory);
        }
    }

    [Fact]
    public async Task ConcurrentReadersObserveSingleWriterHistory()
    {
        var directory = CreateDirectory();
        try
        {
            await using (var store = CreateStore(directory))
            {
                var writes = Enumerable.Range(1, 32).Select(i => store.AppendSourceAsync(CreateSource(i)).AsTask());
                await Task.WhenAll(writes);
                await store.FlushAsync();
                await store.StopAsync();
                var readers = Enumerable.Range(0, 4).Select(async _ => await ToListAsync(store.ReadSourceAsync())).ToArray();
                var results = await Task.WhenAll(readers);
                Assert.All(results, value => Assert.Equal(32, value.Count));
            }
        }
        finally
        {
            RemoveDirectory(directory);
        }
    }

    private static AppendOnlyStorageEngine CreateStore(string directory, long segmentMaxBytes = 4 * 1024 * 1024) => new(new StorageEngineOptions(directory)
    {
        SegmentMaxBytes = segmentMaxBytes,
        FlushInterval = TimeSpan.FromMinutes(1)
    });

    private static SourceEvent CreateSource(long sequence)
    {
        var fileId = FileId.Create("file-1");
        var metadata = new FileMetadata(
            VolumeId.Create("volume-1"), fileId, null, "document.txt", FileKind.File, 10, 4096,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            FileAttributes.Normal, null, null, EventQuality.Exact, true, false);
        var time = new EventTime(DateTimeOffset.UtcNow, TimeSpan.Zero, null, DateTimeOffset.UtcNow, new SourceSequence(sequence), new MountSequence(sequence));
        return new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.LiveUsn, metadata.VolumeId, fileId, null, metadata.Name, null, CanonicalOperation.Create, metadata, time, EventQuality.Exact, null, ProcessAttributionQuality.Unknown, null, null, ImmutableDictionary<string, string>.Empty);
    }

    private static CanonicalEvent CreateCanonical(SourceEvent source) => new(
        source.EventId, source.SchemaVersion, CanonicalOperation.Create, source.Origin, source.VolumeId, source.FileId,
        source.ParentFileId, source.Name, source.OldName, source.Metadata, source.Time, source.Quality,
        source.ProcessInstanceId, source.ProcessQuality, source.MountSessionId, source.OperationCorrelationId, source.Properties);

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> values)
    {
        var result = new List<T>();
        await foreach (var value in values) result.Add(value);
        return result;
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "StorageChronicle.Storage.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void RemoveDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed class FixedCapacityProbe(long available) : IStorageCapacityProbe
    {
        public long GetAvailableBytes(string