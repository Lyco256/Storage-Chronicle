using System.Text.Json;
using Microsoft.Data.Sqlite;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Storage;

internal readonly record struct EventIndexRecord(
    StorageRecordKind Kind,
    long Sequence,
    EventId EventId,
    EventSchemaVersion SchemaVersion,
    EventTime Time,
    FileId? FileId,
    FileId? ParentFileId,
    string? Name,
    byte[] Payload);

internal sealed class SqliteIndex : IAsyncDisposable
{
    private const int CurrentSchemaVersion = 2;
    private readonly string _databasePath;
    private readonly TimeSpan _busyTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection _connection;

    internal SqliteIndex(StorageEngineOptions options)
    {
        _databasePath = Path.Combine(options.StorageDirectory, "index.sqlite");
        _busyTimeout = options.BusyTimeout;
        _connection = CreateConnection(_databasePath, _busyTimeout);
        Initialize();
    }

    internal async ValueTask AppendEventAsync(StorageRecordKind kind, long sequence, EventId eventId, EventSchemaVersion schemaVersion, EventTime time, FileId? fileId, FileId? parentFileId, string? name, byte[] payload, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO event_index
                    (sequence, event_id, kind, schema_major, schema_minor, recorded_utc, source_sequence, file_id, parent_file_id, name, payload)
                VALUES ($sequence, $event_id, $kind, $schema_major, $schema_minor, $recorded_utc, $source_sequence, $file_id, $parent_file_id, $name, $payload);
                INSERT OR IGNORE INTO path_search (file_id, parent_file_id, name, sequence)
                    VALUES ($file_id, $parent_file_id, $name, $sequence);
                """;
            AddParameter(command, "$sequence", sequence);
            AddParameter(command, "$event_id", eventId.ToString());
            AddParameter(command, "$kind", (int)kind);
            AddParameter(command, "$schema_major", schemaVersion.Major);
            AddParameter(command, "$schema_minor", schemaVersion.Minor);
            AddParameter(command, "$recorded_utc", time.RecordedUtc.ToUniversalTime().ToString("O"));
            AddParameter(command, "$source_sequence", time.SourceSequence.Value);
            AddParameter(command, "$file_id", fileId?.ToString());
            AddParameter(command, "$parent_file_id", parentFileId?.ToString());
            AddParameter(command, "$name", name);
            AddParameter(command, "$payload", payload);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask<HashSet<string>> FindExistingEventIdsAsync(StorageRecordKind kind, IReadOnlyCollection<EventId> eventIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eventIds);
        var values = eventIds.Select(value => value.ToString()).Distinct(StringComparer.Ordinal).ToArray();
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (values.Length == 0) return result;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var chunk in values.Chunk(500))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var command = _connection.CreateCommand();
                var parameters = new List<string>(chunk.Length);
                for (var index = 0; index < chunk.Length; index++)
                {
                    var parameter = "$event_id" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    parameters.Add(parameter);
                    AddParameter(command, parameter, chunk[index]);
                }

                command.CommandText = $"SELECT event_id FROM event_index WHERE kind = $kind AND event_id IN ({string.Join(",", parameters)});";
                AddParameter(command, "$kind", (int)kind);
                using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(reader.GetString(0));
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask AppendEventsAsync(IReadOnlyList<EventIndexRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO event_index
                    (sequence, event_id, kind, schema_major, schema_minor, recorded_utc, source_sequence, file_id, parent_file_id, name, payload)
                VALUES ($sequence, $event_id, $kind, $schema_major, $schema_minor, $recorded_utc, $source_sequence, $file_id, $parent_file_id, $name, $payload);
                INSERT OR IGNORE INTO path_search (file_id, parent_file_id, name, sequence)
                    VALUES ($file_id, $parent_file_id, $name, $sequence);
                """;
            AddParameter(command, "$sequence", 0L);
            AddParameter(command, "$event_id", string.Empty);
            AddParameter(command, "$kind", 0);
            AddParameter(command, "$schema_major", 0);
            AddParameter(command, "$schema_minor", 0);
            AddParameter(command, "$recorded_utc", string.Empty);
            AddParameter(command, "$source_sequence", 0L);
            AddParameter(command, "$file_id", null);
            AddParameter(command, "$parent_file_id", null);
            AddParameter(command, "$name", null);
            AddParameter(command, "$payload", Array.Empty<byte>());

            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                command.Parameters["$sequence"].Value = record.Sequence;
                command.Parameters["$event_id"].Value = record.EventId.ToString();
                command.Parameters["$kind"].Value = (int)record.Kind;
                command.Parameters["$schema_major"].Value = record.SchemaVersion.Major;
                command.Parameters["$schema_minor"].Value = record.SchemaVersion.Minor;
                command.Parameters["$recorded_utc"].Value = record.Time.RecordedUtc.ToUniversalTime().ToString("O");
                command.Parameters["$source_sequence"].Value = record.Time.SourceSequence.Value;
                command.Parameters["$file_id"].Value = record.FileId is { } fileId ? fileId.ToString() : DBNull.Value;
                command.Parameters["$parent_file_id"].Value = record.ParentFileId is { } parentFileId ? parentFileId.ToString() : DBNull.Value;
                command.Parameters["$name"].Value = record.Name is { } name ? name : DBNull.Value;
                command.Parameters["$payload"].Value = record.Payload;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            transaction.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask ApplyCanonicalAsync(CanonicalEvent value, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var transaction = _connection.BeginTransaction();
            if (value.FileId is { } fileId)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO current_state
                        (file_id, volume_id, parent_file_id, name, kind, metadata_json, exists_flag, quality, event_time_utc)
                    VALUES ($file_id, $volume_id, $parent_file_id, $name, $kind, $metadata_json, $exists_flag, $quality, $event_time_utc)
                    ON CONFLICT(file_id) DO UPDATE SET
                        volume_id = excluded.volume_id,
                        parent_file_id = excluded.parent_file_id,
                        name = excluded.name,
                        kind = excluded.kind,
                        metadata_json = excluded.metadata_json,
                        exists_flag = excluded.exists_flag,
                        quality = excluded.quality,
                        event_time_utc = excluded.event_time_utc;
                    """;
                AddParameter(command, "$file_id", fileId.ToString());
                AddParameter(command, "$volume_id", value.VolumeId?.ToString());
                AddParameter(command, "$parent_file_id", value.ParentFileId?.ToString());
                AddParameter(command, "$name", value.Name);
                AddParameter(command, "$kind", value.Metadata?.Kind.ToString() ?? FileKind.Unknown.ToString());
                AddParameter(command, "$metadata_json", value.Metadata is null ? null : JsonSerializer.Serialize(value.Metadata));
                AddParameter(command, "$exists_flag", value.Metadata?.Exists ?? value.Operation is not (CanonicalOperation.Delete or CanonicalOperation.Recycle));
                AddParameter(command, "$quality", (int)value.Quality);
                AddParameter(command, "$event_time_utc", value.Time.RecordedUtc.ToUniversalTime().ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using (var projection = _connection.CreateCommand())
            {
                projection.Transaction = transaction;
                projection.CommandText = "INSERT OR REPLACE INTO projection_cache (cache_key, payload, updated_utc) VALUES ($key, $payload, $updated_utc);";
                AddParameter(projection, "$key", $"canonical:{value.EventId}");
                AddParameter(projection, "$payload", JsonSerializer.SerializeToUtf8Bytes(value));
                AddParameter(projection, "$updated_utc", DateTimeOffset.UtcNow.ToString("O"));
                await projection.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            transaction.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask ApplyCanonicalBatchAsync(IReadOnlyList<CanonicalEvent> values, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var transaction = _connection.BeginTransaction();
            using var state = _connection.CreateCommand();
            state.Transaction = transaction;
            state.CommandText = """
                INSERT INTO current_state
                    (file_id, volume_id, parent_file_id, name, kind, metadata_json, exists_flag, quality, event_time_utc)
                VALUES ($file_id, $volume_id, $parent_file_id, $name, $kind, $metadata_json, $exists_flag, $quality, $event_time_utc)
                ON CONFLICT(file_id) DO UPDATE SET
                    volume_id = excluded.volume_id,
                    parent_file_id = excluded.parent_file_id,
                    name = excluded.name,
                    kind = excluded.kind,
                    metadata_json = excluded.metadata_json,
                    exists_flag = excluded.exists_flag,
                    quality = excluded.quality,
                    event_time_utc = excluded.event_time_utc;
                """;
            AddParameter(state, "$file_id", string.Empty);
            AddParameter(state, "$volume_id", DBNull.Value);
            AddParameter(state, "$parent_file_id", DBNull.Value);
            AddParameter(state, "$name", DBNull.Value);
            AddParameter(state, "$kind", string.Empty);
            AddParameter(state, "$metadata_json", DBNull.Value);
            AddParameter(state, "$exists_flag", false);
            AddParameter(state, "$quality", 0);
            AddParameter(state, "$event_time_utc", string.Empty);

            using var projection = _connection.CreateCommand();
            projection.Transaction = transaction;
            projection.CommandText = "INSERT OR REPLACE INTO projection_cache (cache_key, payload, updated_utc) VALUES ($key, $payload, $updated_utc);";
            AddParameter(projection, "$key", string.Empty);
            AddParameter(projection, "$payload", Array.Empty<byte>());
            AddParameter(projection, "$updated_utc", string.Empty);

            foreach (var value in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (value.FileId is { } fileId)
                {
                    state.Parameters["$file_id"].Value = fileId.ToString();
                    state.Parameters["$volume_id"].Value = value.VolumeId is { } volumeId ? volumeId.ToString() : DBNull.Value;
                    state.Parameters["$parent_file_id"].Value = value.ParentFileId is { } parentFileId ? parentFileId.ToString() : DBNull.Value;
                    state.Parameters["$name"].Value = value.Name is { } name ? name : DBNull.Value;
                    state.Parameters["$kind"].Value = value.Metadata?.Kind.ToString() ?? FileKind.Unknown.ToString();
                    state.Parameters["$metadata_json"].Value = value.Metadata is null ? DBNull.Value : JsonSerializer.Serialize(value.Metadata);
                    state.Parameters["$exists_flag"].Value = value.Metadata?.Exists ?? value.Operation is not (CanonicalOperation.Delete or CanonicalOperation.Recycle);
                    state.Parameters["$quality"].Value = (int)value.Quality;
                    state.Parameters["$event_time_utc"].Value = value.Time.RecordedUtc.ToUniversalTime().ToString("O");
                    await state.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                projection.Parameters["$key"].Value = $"canonical:{value.EventId}";
                projection.Parameters["$payload"].Value = JsonSerializer.SerializeToUtf8Bytes(value);
                projection.Parameters["$updated_utc"].Value = DateTimeOffset.UtcNow.ToString("O");
                await projection.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            transaction.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask<FileStateSnapshot> GetSnapshotAsync(DateTimeOffset atUtc, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT metadata_json, exists_flag, quality FROM current_state ORDER BY file_id;";
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var entries = new List<FileStateEntry>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var metadata = reader.IsDBNull(0) ? null : JsonSerializer.Deserialize<FileMetadata>(reader.GetString(0));
                if (metadata is null) continue;
                var exists = reader.GetBoolean(1);
                var quality = (EventQuality)reader.GetInt32(2);
                entries.Add(new FileStateEntry(metadata with { Exists = exists }, null, !exists, quality));
            }

            return new FileStateSnapshot(atUtc, entries, Array.Empty<FileStateEntry>());
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask<IReadOnlyList<byte[]>> ReadEventPayloadsAsync(StorageRecordKind kind, int offset, int limit, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            var predicates = new List<string> { "kind = $kind" };
            AddParameter(command, "$kind", (int)kind);
            if (fromUtc is { } from)
            {
                predicates.Add("recorded_utc >= $from_utc");
                AddParameter(command, "$from_utc", from.ToUniversalTime().ToString("O"));
            }

            if (toUtc is { } to)
            {
                predicates.Add("recorded_utc <= $to_utc");
                AddParameter(command, "$to_utc", to.ToUniversalTime().ToString("O"));
            }

            command.CommandText = $"SELECT payload FROM event_index WHERE {string.Join(" AND ", predicates)} ORDER BY recorded_utc, source_sequence, sequence LIMIT $limit OFFSET $offset;";
            AddParameter(command, "$limit", limit);
            AddParameter(command, "$offset", offset);
            var result = new List<byte[]>(limit);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add((byte[])reader[0]);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask<int> CountEventsAsync(StorageRecordKind kind, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            var predicates = new List<string> { "kind = $kind" };
            AddParameter(command, "$kind", (int)kind);
            if (fromUtc is { } from)
            {
                predicates.Add("recorded_utc >= $from_utc");
                AddParameter(command, "$from_utc", from.ToUniversalTime().ToString("O"));
            }

            if (toUtc is { } to)
            {
                predicates.Add("recorded_utc <= $to_utc");
                AddParameter(command, "$to_utc", to.ToUniversalTime().ToString("O"));
            }

            command.CommandText = $"SELECT COUNT(*) FROM event_index WHERE {string.Join(" AND ", predicates)};";
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask StoreFinalSequenceAsync(long sequence, long sourceSequence, RecordingState state, string? reason, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO storage_metadata (key, value) VALUES ('last_sequence', $sequence), ('last_source_sequence', $source_sequence), ('recording_state', $state), ('recording_reason', $reason);";
            AddParameter(command, "$sequence", sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddParameter(command, "$source_sequence", sourceSequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddParameter(command, "$state", state.ToString());
            AddParameter(command, "$reason", reason ?? string.Empty);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask RecreateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _connection.Close();
            _connection.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
            {
                if (File.Exists(path)) File.Delete(path);
            }

            _connection = CreateConnection(_databasePath, _busyTimeout);
            Initialize();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Initialize()
    {
        ExecutePragma("PRAGMA journal_mode=WAL;");
        ExecutePragma("PRAGMA synchronous=FULL;");
        ExecutePragma("PRAGMA foreign_keys=ON;");
        ExecutePragma($"PRAGMA busy_timeout={(long)_busyTimeout.TotalMilliseconds};");
        var version = ReadUserVersion();
        if (version > CurrentSchemaVersion) throw new StorageException($"SQLite schema version {version} is newer than {CurrentSchemaVersion}.");
        if (version == 0) ApplyMigrationOne();
        else if (version == 1) ApplyMigrationTwo();
    }

    private void ApplyMigrationOne()
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS event_index (
                sequence INTEGER PRIMARY KEY,
                event_id TEXT NOT NULL,
                kind INTEGER NOT NULL,
                schema_major INTEGER NOT NULL,
                schema_minor INTEGER NOT NULL,
                recorded_utc TEXT NOT NULL,
                source_sequence INTEGER NOT NULL,
                file_id TEXT,
                parent_file_id TEXT,
                name TEXT,
                payload BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS path_search (file_id TEXT NOT NULL, parent_file_id TEXT, name TEXT, sequence INTEGER NOT NULL, PRIMARY KEY (file_id, sequence));
            CREATE TABLE IF NOT EXISTS current_state (
                file_id TEXT PRIMARY KEY,
                volume_id TEXT,
                parent_file_id TEXT,
                name TEXT,
                kind TEXT NOT NULL,
                metadata_json TEXT,
                exists_flag INTEGER NOT NULL,
                quality INTEGER NOT NULL,
                event_time_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS process (process_instance_id TEXT PRIMARY KEY, payload BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS volume (volume_id TEXT PRIMARY KEY, payload BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS mount_session (mount_session_id TEXT PRIMARY KEY, payload BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS projection_cache (cache_key TEXT PRIMARY KEY, payload BLOB NOT NULL, updated_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS storage_metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_event_index_recorded_utc ON event_index (recorded_utc);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_event_index_event_kind ON event_index (event_id, kind);
            CREATE INDEX IF NOT EXISTS ix_path_search_name ON path_search (name);
            INSERT INTO schema_migrations(version, applied_utc) VALUES (1, $now);
            INSERT INTO schema_migrations(version, applied_utc) VALUES (2, $now);
            PRAGMA user_version = 2;
            """;
        AddParameter(command, "$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    internal async ValueTask<bool> ContainsEventAsync(StorageRecordKind kind, EventId eventId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM event_index WHERE event_id = $event_id AND kind = $kind LIMIT 1;";
            AddParameter(command, "$event_id", eventId.ToString());
            AddParameter(command, "$kind", (int)kind);
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ApplyMigrationTwo()
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE event_index RENAME TO event_index_v1;
            CREATE TABLE event_index (
                sequence INTEGER PRIMARY KEY,
                event_id TEXT NOT NULL,
                kind INTEGER NOT NULL,
                schema_major INTEGER NOT NULL,
                schema_minor INTEGER NOT NULL,
                recorded_utc TEXT NOT NULL,
                source_sequence INTEGER NOT NULL,
                file_id TEXT,
                parent_file_id TEXT,
                name TEXT,
                payload BLOB NOT NULL);
            INSERT INTO event_index (sequence, event_id, kind, schema_major, schema_minor, recorded_utc, source_sequence, file_id, parent_file_id, name, payload)
                SELECT sequence, event_id, kind, schema_major, schema_minor, recorded_utc, source_sequence, file_id, parent_file_id, name, payload FROM event_index_v1;
            DROP TABLE event_index_v1;
            CREATE INDEX IF NOT EXISTS ix_event_index_recorded_utc ON event_index (recorded_utc);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_event_index_event_kind ON event_index (event_id, kind);
            INSERT INTO schema_migrations(version, applied_utc) VALUES (2, $now);
            PRAGMA user_version = 2;
            """;
        AddParameter(command, "$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private int ReadUserVersion()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private void ExecutePragma(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection CreateConnection(string path, TimeSpan busyTimeout)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(busyTimeout.TotalSeconds))
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static void AddParameter(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    public async ValueTask DisposeAsync()
    {
        _connection.Close();
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        _gate.Dispose();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}
