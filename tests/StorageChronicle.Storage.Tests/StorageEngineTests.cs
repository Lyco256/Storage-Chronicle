using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
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
                Assert.Equal(1, await store.CountEventsAsync(canonical: false));
                Assert.Equal(1, await store.CountEventsAsync(canonical: true));
                Assert.Single(await store.ReadCanonicalPageAsync(0, 10));
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
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "index.sqlite"), Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('event_index', 'current_state', 'path_search', 'process', 'volume', 'mount_session', 'projection_cache', 'process_lifecycle') ORDER BY name;";
                using var reader = await command.ExecuteReaderAsync();
                var tables = new List<string>();
                while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
                Assert.Equal(8, tables.Count);
                connection.Close();
            }
        }
        finally
        {
            RemoveDirectory(directory);
        }
    }

    [Fact]
    public async Task ProcessLifecycleFactsRemainSeparateAndRebuildFromAppendLog()
    {
        var directory = CreateDirectory();
        var processId = ProcessInstanceId.Create("process-lifecycle-replay");
        var occurred = DateTimeOffset.UtcNow.AddMinutes(-1);
        var value = new ProcessLifecycleEvent(EventId.New(), EventSchemaVersion.Current, processId, ProcessLifecycleTransition.Exited, occurred, DateTimeOffset.UtcNow, EventOrigin.Etw, EventQuality.Exact);
        try
        {
            await using (var store = CreateStore(directory))
            {
                await store.AppendProcessLifecycleEventAsync(value);
                Assert.Equal(0, await store.CountEventsAsync(canonical: false));
                Assert.Equal(0, await store.CountEventsAsync(canonical: true));
                Assert.Equal(value, Assert.Single(await store.ReadProcessLifecycleEventsAsync([processId], occurred.AddSeconds(-1), occurred.AddSeconds(1))));
                await store.StopAsync();
            }

            foreach (var path in new[] { Path.Combine(directory, "index.sqlite"), Path.Combine(directory, "index.sqlite-wal"), Path.Combine(directory, "index.sqlite-shm") })
                if (File.Exists(path)) File.Delete(path);

            await using (var recovered = CreateStore(directory))
            {
                await recovered.RebuildSqliteAsync();
                Assert.Equal(value, Assert.Single(await recovered.ReadProcessLifecycleEventsAsync([processId], occurred.AddSeconds(-1), occurred.AddSeconds(1))));
            }
        }
        finally
        {
            RemoveDirectory(directory);
        }
    }

    [Fact]
    public async Task ProcessLifecycleAppendHonorsCancellationAndCapacityStop()
    {
        var directory = CreateDirectory();
        var value = new ProcessLifecycleEvent(EventId.New(), EventSchemaVersion.Current, ProcessInstanceId.Create("process-lifecycle-failure"), ProcessLifecycleTransition.Exited, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, EventOrigin.Etw, EventQuality.Exact);
        try
        {
            await using (var store = CreateStore(directory))
            {
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.AppendProcessLifecycleEventAsync(value, cancellation.Token));
                Assert.Empty(await store.ReadProcessLifecycleEventsAsync([value.ProcessInstanceId], value.OccurredUtc.AddSeconds(-1), value.OccurredUtc.AddSeconds(1)));
            }

            await using var capacityStopped = new AppendOnlyStorageEngine(new StorageEngineOptions(Path.Combine(directory, "capacity"))
            {
                MinimumFreeBytes = 1,
                CapacityProbe = new FixedCapacityProbe(0)
            });
            await Assert.ThrowsAsync<StorageCapacityException>(async () => await capacityStopped.AppendProcessLifecycleEventAsync(value));
            Assert.Equal(RecordingState.CapacityStopped, capacityStopped.Status.State);
            Assert.Empty(await capacityStopped.ReadProcessLifecycleEventsAsync([value.ProcessInstanceId], value.OccurredUtc.AddSeconds(-1), value.OccurredUtc.AddSeconds(1)));
        }
        finally
        {
            RemoveDirectory(directory);
        }
    }

    [Fact]
    public async Task ProcessLifecycleIndexRecoversMissingRowsFromImmutableSegmentsOnOpen()
    {
        var directory = CreateDirectory();
        var processId = ProcessInstanceId.Create("process-lifecycle-index-recovery");
        var occurred = DateTimeOffset.UtcNow.AddMinutes(-1);
        var value = new ProcessLifecycleEvent(EventId.New(), EventSchemaVersion.Current, processId, ProcessLifecycleTransition.Exited, occurred, DateTimeOffset.UtcNow, EventOrigin.Etw, EventQuality.Exact);
        try
        {
            await using (var store = CreateStore(directory))
            {
                await store.AppendProcessLifecycleEventAsync(value);
                await store.StopAsync();
            }

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "index.sqlite"), Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM process_lifecycle WHERE event_id = $eventId;";
                command.Parameters.AddWithValue("$eventId", value.EventId.ToString());
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            await using var recovered = CreateStore(directory);
            Assert.Equal(value, Assert.Single(await recovered.ReadProcessLifecycleEventsAsync([processId], occurred.AddSeconds(-1), occurred.AddSeconds(1))));
        }
        finally
        {
            RemoveDirectory(directory);
        }
    }

    [Fact]
    public async Task CorruptProcessLifecycleIndexPayloadIsObservableAndCanBeRebuiltFromSegments()
    {
        var directory = CreateDirectory();
        var processId = ProcessInstanceId.Create("process-lifecycle-corruption");
        var occurred = DateTimeOffset.UtcNow.AddMinutes(-1);
        var value = new ProcessLifecycleEvent(EventId.New(), EventSchemaVersion.Current, processId, ProcessLifecycleTransition.Exited, occurred, DateTimeOffset.UtcNow, EventOrigin.Etw, EventQuality.Exact);
        try
        {
            await using (var store = CreateStore(directory))
            {
                await store.AppendProcessLifecycleEventAsync(value);
                await store.StopAsync();
            }

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "index.sqlite"), Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE process_lifecycle SET payload = $payload WHERE event_id = $eventId;";
                command.Parameters.AddWithValue("$payload", new byte[] { 0x7b, 0x7d, 0x7d });
                command.Parameters.AddWithValue("$eventId", value.EventId.ToString());
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            await using var recovered = CreateStore(directory);
            await Assert.ThrowsAsync<JsonException>(async () => await recovered.ReadProcessLifecycleEventsAsync([processId], occurred.AddSeconds(-1), occurred.AddSeconds(1)));
            await recovered.RebuildSqliteAsync();
            Assert.Equal(value, Assert.Single(await recovered.ReadProcessLifecycleEventsAsync([processId], occurred.AddSeconds(-1), occurred.AddSeconds(1))));
        }
        finally
        {
            RemoveDirectory(directory);
        }
    }

    [Fact]
    public async Task SchemaVersionTwoMigratesToLifecycleIndexWithoutLosingEventRows()
    {
        var directory = CreateDirectory();
        var processId = ProcessInstanceId.Create("process-lifecycle-v2-migration");
        var occurred = DateTimeOffset.UtcNow;
        var lifecycle = new ProcessLifecycleEvent(EventId.New(), EventSchemaVersion.Current, processId, ProcessLifecycleTransition.Exited, occurred, occurred, EventOrigin.Etw, EventQuality.Exact);
        try
        {
            await using (var store = CreateStore(directory))
            {
                await store.AppendSourceAsync(CreateSource(1));
                await store.StopAsync();
            }

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "index.sqlite"), Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE process_lifecycle; DELETE FROM schema_migrations WHERE version = 3; PRAGMA user_version = 2;";
                await command.ExecuteNonQueryAsync();
            }

            await using var migrated = CreateStore(directory);
            Assert.Equal(1, await migrated.CountEventsAsync(canonical: false));
            await migrated.AppendProcessLifecycleEventAsync(lifecycle);
            Assert.Equal(lifecycle, Assert.Single(await migrated.ReadProcessLifecycleEventsAsync([processId], occurred.AddSeconds(-1), occurred.AddSeconds(1))));
        }
        finally
        {
            RemoveDirectory(directory);
        }
    }

    [Fact]
    public async Task SchemaVersionOneMigratesThroughCurrentLifecycleSchemaWithoutLosingEventRows()
    {
        var directory = CreateDirectory();
        var processId = ProcessInstanceId.Create("process-lifecycle-v1-migration");
        var occurred = DateTimeOffset.UtcNow;
        var lifecycle = new ProcessLifecycleEvent(EventId.New(), EventSchemaVersion.Current, processId, ProcessLifecycleTransition.Exited, occurred, occurred, EventOrigin.Etw, EventQuality.Exact);
        try
        {
            await using (var store = CreateStore(directory))
            {
                await store.AppendSourceAsync(CreateSource(1));
                await store.StopAsync();
            }

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "index.sqlite"), Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE process_lifecycle; DELETE FROM schema_migrations WHERE version IN (2, 3); PRAGMA user_version = 1;";
                await command.ExecuteNonQueryAsync();
            }

            await using var migrated = CreateStore(directory);
            Assert.Equal(1, await migrated.CountEventsAsync(canonical: false));
            await migrated.AppendProcessLifecycleEventAsync(lifecycle);
            Assert.Equal(lifecycle, Assert.Single(await migrated.ReadProcessLifecycleEventsAsync([processId], occurred.AddSeconds(-1), occurred.AddSeconds(1))));
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
                await recovered.StopAsync();
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
    public async Task IncompleteOpenSegmentIsReadWithoutTruncationAndNeverReopenedForAppend()
    {
        var directory = CreateDirectory();
        try
        {
            string damagedPath;
            await using (var initial = new AppendOnlyStorageEngine(new StorageEngineOptions(directory)
            {
                CompressClosedSegments = false,
                FlushInterval = TimeSpan.FromMinutes(1)
            }))
            {
                await initial.AppendSourceAsync(CreateSource(1));
                await initial.FlushAsync();
            }

            damagedPath = Assert.Single(Directory.GetFiles(directory, "*.open"));
            await using (var stream = new FileStream(damagedPath, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(new byte[] { 32, 0, 0, 0, 1, 0 }, TestContext.Current.CancellationToken);
            }
            var originalDamagedBytes = await File.ReadAllBytesAsync(damagedPath, TestContext.Current.CancellationToken);

            await using (var recovered = new AppendOnlyStorageEngine(new StorageEngineOptions(directory)
            {
                CompressClosedSegments = false,
                FlushInterval = TimeSpan.FromMinutes(1)
            }))
            {
                Assert.Single(await ToListAsync(recovered.ReadSourceAsync()));
                await recovered.AppendSourceAsync(CreateSource(2));
                await recovered.FlushAsync();
                Assert.Equal(originalDamagedBytes, await File.ReadAllBytesAsync(damagedPath, TestContext.Current.CancellationToken));
                Assert.Equal(2, Directory.GetFiles(directory, "*.open").Length);
            }
        }
        finally
        {
            RemoveDirectory(directory);
        }
    }

    [Fact]
    public async Task BoundedCanonicalBatchKeepsOrderDeduplicatesAndRebuilds()
    {
        var directory = CreateDirectory();
        try
        {
            var first = CreateCanonical(CreateSource(1));
            var second = CreateCanonical(CreateSource(2));
            await using (var store = CreateStore(directory))
            {
                await store.AppendCanonicalBatchAsync(new[] { first, first, second });
                Assert.Equal(2, await store.CountEventsAsync(canonical: true));
                await store.StopAsync();
                var recorded = await ToListAsync(store.ReadCanonicalAsync());
                Assert.Equal(new[] { first.EventId, second.EventId }, recorded.Select(value => value.EventId).ToArray());
            }

            File.Delete(Path.Combine(directory, "index.sqlite"));
            File.Delete(Path.Combine(directory, "index.sqlite-wal"));
            File.Delete(Path.Combine(directory, "index.sqlite-shm"));
            await using var recovered = CreateStore(directory);
            await recovered.RebuildSqliteAsync();
            Assert.Equal(2, await recovered.CountEventsAsync(canonical: true));
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

    [Fact]
    public async Task RelocationFlushesCopiesSegmentsRebuildsIndexAndKeepsOldHistory()
    {
        var directory = CreateDirectory();
        var destination = CreateDirectory();
        AssertFixtureOwnership(destination);
        Directory.Delete(destination);
        try
        {
            await using var store = CreateStore(directory);
            var source = CreateSource(1);
            await store.AppendSourceAsync(source);
            var canonical = CreateCanonical(source);
            await store.AppendCanonicalAsync(canonical);
            await store.ApplyAsync(canonical);
            await store.RelocateAsync(destination);

            Assert.Equal(Path.GetFullPath(destination), store.StorageDirectory);
            Assert.Single(await ToListAsync(store.ReadSourceAsync()));
            Assert.Single(await ToListAsync(store.ReadCanonicalAsync()));
            Assert.Single((await store.GetSnapshotAsync(DateTimeOffset.UtcNow)).Entries);
            Assert.NotEmpty(Directory.GetFiles(directory, "segment-*"));
            Assert.NotEmpty(Directory.GetFiles(destination, "segment-*"));
        }
        finally
        {
            RemoveDirectory(directory);
            RemoveDirectory(destination);
        }
    }

    [Fact]
    public async Task RelocationRefusesExistingEmptyDestinationWithoutRemovingIt()
    {
        var directory = CreateDirectory();
        var destination = CreateDirectory();
        try
        {
            await using var store = CreateStore(directory);
            await Assert.ThrowsAsync<IOException>(() => store.RelocateAsync(destination).AsTask());

            Assert.True(Directory.Exists(destination));
            Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
            Assert.Equal(Path.GetFullPath(directory), store.StorageDirectory);
        }
        finally
        {
            RemoveDirectory(directory);
            RemoveDirectory(destination);
        }
    }

    [Fact]
    public void UnownedExistingHistoryIsRefusedWithoutDeletingOrChangingFiles()
    {
        var directory = CreateDirectory();
        var unexpectedIndex = Path.Combine(directory, "index.sqlite");
        byte[] original = [0x53, 0x43, 0x01, 0x7F];
        try
        {
            File.WriteAllBytes(unexpectedIndex, original);

            Assert.Throws<IOException>(() => CreateStore(directory));

            Assert.Equal(original, File.ReadAllBytes(unexpectedIndex));
            Assert.False(File.Exists(Path.Combine(directory, ".storage-chronicle-history-owner.json")));
        }
        finally
        {
            RemoveDirectory(directory);
        }
    }

    [Fact]
    public async Task UnknownEntryInOwnedHistoryIsPreservedAndPreventsRecovery()
    {
        var directory = CreateDirectory();
        var unexpected = Path.Combine(directory, "user-notes.txt");
        byte[] original = [0x75, 0x73, 0x65, 0x72];
        try
        {
            await using (CreateStore(directory)) { }
            await File.WriteAllBytesAsync(unexpected, original);

            Assert.Throws<IOException>(() => CreateStore(directory));

            Assert.Equal(original, await File.ReadAllBytesAsync(unexpected));
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
        var runId = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "StorageChronicle.Storage.Tests", runId);
        Directory.CreateDirectory(root);
        using (var marker = new FileStream(Path.Combine(root, ".test-owner.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            JsonSerializer.Serialize(marker, new { Schema = "StorageChronicle.TestFixtureOwner.v1", RunId = runId });
        var history = Path.Combine(root, "history");
        Directory.CreateDirectory(history);
        return history;
    }

    private static void RemoveDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetDirectoryName(fullPath) ?? throw new IOException("The storage test fixture has no owner directory.");
        if (!Directory.Exists(root)) return;
        var expectedParent = Path.Combine(Path.GetTempPath(), "StorageChronicle.Storage.Tests");
        if (!string.Equals(Path.GetDirectoryName(root), expectedParent, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The storage test fixture escaped its dedicated temp parent.");
        AssertFixtureOwnership(path);
        for (var attempt = 0; attempt < 60 && Directory.Exists(root); attempt++)
        {
            AssertFixtureOwnership(path);
            EnsureNoReparsePoints(root);
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) when (attempt < 59)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(100);
            }
        }
        if (Directory.Exists(root)) throw new IOException("The marked storage test fixture could not be removed.");
    }

    private static void AssertFixtureOwnership(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetDirectoryName(fullPath) ?? throw new IOException("The storage test fixture has no owner directory.");
        var expectedParent = Path.Combine(Path.GetTempPath(), "StorageChronicle.Storage.Tests");
        var runId = Path.GetFileName(root);
        if (!string.Equals(Path.GetDirectoryName(root), expectedParent, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(runId, "N", out _))
            throw new IOException("The storage test fixture escaped its dedicated temp parent or run GUID.");
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".test-owner.json")));
        if (marker.RootElement.GetProperty("Schema").GetString() != "StorageChronicle.TestFixtureOwner.v1" || marker.RootElement.GetProperty("RunId").GetString() != runId)
            throw new IOException("The storage fixture owner marker does not match this run.");
    }

    private static void EnsureNoReparsePoints(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException($"A storage fixture path is a reparse point: {directory}");
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException($"A reparse point was found in the storage fixture: {entry}");
            if (Directory.Exists(entry)) EnsureNoReparsePoints(entry);
        }
    }

    private sealed class FixedCapacityProbe(long available) : IStorageCapacityProbe
    {
        public long GetAvailableBytes(string storageDirectory) => available;
    }
}
