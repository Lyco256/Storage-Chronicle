using System.Text.Json;
using Microsoft.Data.Sqlite;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Storage;

internal sealed class SqliteIndex : IAsyncDisposable
{
    private const int CurrentSchemaVersion = 1;
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
                event_id TEXT NOT NULL UNIQUE,
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
            CREATE INDEX IF NOT EXISTS ix_path_search_name ON path_search (name);
            INSERT INTO schema_migrations(version, applied_utc) VALUES (1, $now);
            PRAGMA user_version = 1;
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
