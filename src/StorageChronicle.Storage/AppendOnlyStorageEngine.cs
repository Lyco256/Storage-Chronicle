using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Storage;

/// <summary>Provides immutable append storage with a rebuildable SQLite index and state cache.</summary>
public sealed class AppendOnlyStorageEngine : IEventStore, IStateStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);
    private readonly StorageEngineOptions _options;
    private readonly SegmentLog _segments;
    private readonly SqliteIndex _index;
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _flushLoop;
    private long _nextSequence;
    private long _lastSourceSequence;
    private RecordingStatus _status = new(RecordingState.Running, 0, 0, null);
    private int _disposed;

    /// <summary>Opens or creates an append store at the configured path.</summary>
    public AppendOnlyStorageEngine(StorageEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        Directory.CreateDirectory(options.StorageDirectory);
        _segments = new SegmentLog(options);
        _segments.SegmentSkipped += issue => SegmentSkipped?.Invoke(issue);
        try
        {
            _index = new SqliteIndex(options);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException or IOException)
        {
            SqliteConnection.ClearAllPools();
            RemoveSqliteFiles(options.StorageDirectory);
            _index = new SqliteIndex(options);
        }

        foreach (var record in _segments.ReadAllRecords())
        {
            _nextSequence = Math.Max(_nextSequence, record.Sequence);
            if (record.Kind == StorageRecordKind.SourceEvent)
            {
                try
                {
                    var source = JsonSerializer.Deserialize<SourceEvent>(record.Payload, JsonOptions);
                    if (source is not null) _lastSourceSequence = Math.Max(_lastSourceSequence, source.Time.SourceSequence.Value);
                }
                catch (JsonException)
                {
                    // A segment with valid framing but an invalid event payload remains readable as a segment issue.
                }
            }
        }

        _flushLoop = Task.Run(FlushLoopAsync);
    }

    /// <summary>Raised when recovery skips a corrupt segment while retaining healthy segments.</summary>
    public event Action<SegmentIssue>? SegmentSkipped;

    /// <summary>Raised whenever recording changes state or a flush fails.</summary>
    public event Action<RecordingStatus>? StatusChanged;

    /// <summary>Returns the current durable recording state.</summary>
    public RecordingStatus Status => _status;

    /// <inheritdoc />
    public async ValueTask AppendSourceAsync(SourceEvent value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        await AppendAsync(StorageRecordKind.SourceEvent, value.SchemaVersion, value.Time.SourceSequence.Value, value, value.EventId, value.Time, value.FileId, value.ParentFileId, value.Name, cancellationToken).ConfigureAwait(false);
        if (value.Hint is CanonicalOperation.Delete or CanonicalOperation.Rename or CanonicalOperation.Move || value.Properties.ContainsKey("media_removed"))
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask AppendCanonicalAsync(CanonicalEvent value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.IsReadOnlyObservation) throw new InvalidOperationException("Read-only observations are not durable events.");
        await AppendAsync(StorageRecordKind.CanonicalEvent, value.SchemaVersion, value.Time.SourceSequence.Value, value, value.EventId, value.Time, value.FileId, value.ParentFileId, value.Name, cancellationToken).ConfigureAwait(false);
        if (value.Operation is CanonicalOperation.Delete or CanonicalOperation.Rename or CanonicalOperation.Move)
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads source events directly from segments without opening SQLite.</summary>
    public async IAsyncEnumerable<SourceEvent> ReadSourceAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var record in _segments.ReadAllRecords())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.Kind != StorageRecordKind.SourceEvent) continue;
            SourceEvent? value;
            try
            {
                value = JsonSerializer.Deserialize<SourceEvent>(record.Payload, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (value is not null) yield return value;
            await Task.Yield();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<CanonicalEvent> ReadCanonicalAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var record in _segments.ReadAllRecords())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.Kind != StorageRecordKind.CanonicalEvent) continue;
            CanonicalEvent? value;
            try
            {
                value = JsonSerializer.Deserialize<CanonicalEvent>(record.Payload, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (value is not null) yield return value;
            await Task.Yield();
        }
    }

    /// <summary>Lists healthy segments while skipping any segment that fails format, CRC, or decompression validation.</summary>
    public ValueTask<IReadOnlyList<SegmentInfo>> EnumerateHealthySegmentsAsync(CancellationToken cancellationToken = default) => _segments.EnumerateHealthySegmentsAsync(cancellationToken);

    /// <summary>Flushes the active segment and SQLite metadata to durable storage.</summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var failure = exception is StorageFlushException storageFlush
                ? storageFlush
                : new StorageFlushException("The append log could not be flushed.", exception);
            SetStatus(new RecordingStatus(RecordingState.Stopped, _nextSequence, _lastSourceSequence, failure.Message));
            throw failure;
        }
        finally
        {
            _writerGate.Release();
        }
    }

    /// <summary>Stops new writes, prioritizes a final flush, closes the active segment, and records the final sequence.</summary>
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_status.State is RecordingState.Completed) return;
            _lifetime.Cancel();
            await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
            await _segments.CloseAsync(cancellationToken).ConfigureAwait(false);
            await _index.StoreFinalSequenceAsync(_nextSequence, _lastSourceSequence, RecordingState.Completed, null, cancellationToken).ConfigureAwait(false);
            SetStatus(new RecordingStatus(RecordingState.Completed, _nextSequence, _lastSourceSequence, null));
        }
        finally
        {
            _writerGate.Release();
        }

        await _flushLoop.ConfigureAwait(false);
    }

    /// <summary>Recreates only the SQLite index and state cache from immutable segments.</summary>
    public async ValueTask RebuildSqliteAsync(CancellationToken cancellationToken = default)
    {
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _index.RecreateAsync(cancellationToken).ConfigureAwait(false);
            foreach (var record in _segments.ReadAllRecords())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (record.Kind == StorageRecordKind.SourceEvent)
                {
                    var source = JsonSerializer.Deserialize<SourceEvent>(record.Payload, JsonOptions);
                    if (source is not null)
                    {
                        await _index.AppendEventAsync(record.Kind, record.Sequence, source.EventId, source.SchemaVersion, source.Time, source.FileId, source.ParentFileId, source.Name, record.Payload, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    var canonical = JsonSerializer.Deserialize<CanonicalEvent>(record.Payload, JsonOptions);
                    if (canonical is not null)
                    {
                        await _index.AppendEventAsync(record.Kind, record.Sequence, canonical.EventId, canonical.SchemaVersion, canonical.Time, canonical.FileId, canonical.ParentFileId, canonical.Name, record.Payload, cancellationToken).ConfigureAwait(false);
                        await _index.ApplyCanonicalAsync(canonical, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            await _index.StoreFinalSequenceAsync(_nextSequence, _lastSourceSequence, _status.State, _status.Reason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask ApplyAsync(CanonicalEvent value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        EnsureRunning();
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _index.ApplyCanonicalAsync(value, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            SetStatus(new RecordingStatus(RecordingState.Stopped, _nextSequence, _lastSourceSequence, exception.Message));
            throw new StorageException("SQLite state update failed; immutable segments remain authoritative.", exception);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask<FileStateSnapshot> GetSnapshotAsync(DateTimeOffset atUtc, CancellationToken cancellationToken = default) => _index.GetSnapshotAsync(atUtc, cancellationToken);

    /// <summary>Reads a bounded page of canonical event payloads from the rebuildable SQLite index.</summary>
    public async ValueTask<IReadOnlyList<CanonicalEvent>> ReadCanonicalPageAsync(int offset, int limit, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, CancellationToken cancellationToken = default)
    {
        var payloads = await _index.ReadEventPayloadsAsync(StorageRecordKind.CanonicalEvent, offset, limit, fromUtc, toUtc, cancellationToken).ConfigureAwait(false);
        var result = new List<CanonicalEvent>(payloads.Count);
        foreach (var payload in payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = JsonSerializer.Deserialize<CanonicalEvent>(payload, JsonOptions);
            if (value is not null) result.Add(value);
        }

        return result;
    }

    /// <summary>Reads a bounded page of source event payloads from the rebuildable SQLite index.</summary>
    public async ValueTask<IReadOnlyList<SourceEvent>> ReadSourcePageAsync(int offset, int limit, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, CancellationToken cancellationToken = default)
    {
        var payloads = await _index.ReadEventPayloadsAsync(StorageRecordKind.SourceEvent, offset, limit, fromUtc, toUtc, cancellationToken).ConfigureAwait(false);
        var result = new List<SourceEvent>(payloads.Count);
        foreach (var payload in payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = JsonSerializer.Deserialize<SourceEvent>(payload, JsonOptions);
            if (value is not null) result.Add(value);
        }

        return result;
    }

    /// <summary>Counts indexed event records without materializing their payloads.</summary>
    public ValueTask<int> CountEventsAsync(bool canonical, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, CancellationToken cancellationToken = default)
        => _index.CountEventsAsync(canonical ? StorageRecordKind.CanonicalEvent : StorageRecordKind.SourceEvent, fromUtc, toUtc, cancellationToken);

    private async ValueTask AppendAsync<T>(StorageRecordKind kind, EventSchemaVersion schemaVersion, long sourceSequence, T value, EventId eventId, EventTime time, FileId? fileId, FileId? parentFileId, string? name, CancellationToken cancellationToken)
    {
        EnsureRunning();
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureCapacityAsync(cancellationToken).ConfigureAwait(false);
            var sequence = checked(++_nextSequence);
            var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            var segmentAppendCompleted = false;
            try
            {
                await _segments.AppendAsync(kind, schemaVersion.Major, schemaVersion.Minor, sequence, payload, cancellationToken).ConfigureAwait(false);
                segmentAppendCompleted = true;
                _lastSourceSequence = Math.Max(_lastSourceSequence, sourceSequence);
                await _index.AppendEventAsync(kind, sequence, eventId, schemaVersion, time, fileId, parentFileId, name, payload, CancellationToken.None).ConfigureAwait(false);
                await _index.StoreFinalSequenceAsync(sequence, _lastSourceSequence, RecordingState.Running, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
            {
                var capacity = exception is StorageCapacityException || IsCapacityFailure(exception);
                var status = new RecordingStatus(capacity ? RecordingState.CapacityStopped : RecordingState.Stopped, segmentAppendCompleted ? sequence : sequence - 1, _lastSourceSequence, exception.Message);
                SetStatus(status);
                await TryStoreStatusAsync(status).ConfigureAwait(false);
                if (capacity) throw new StorageCapacityException("Recording stopped because durable storage capacity was exhausted.", exception);
                throw new StorageException("Recording stopped after a durable storage failure.", exception);
            }
        }
        finally
        {
            _writerGate.Release();
        }
    }

    private async ValueTask FlushCoreAsync(CancellationToken cancellationToken)
    {
        await _segments.FlushAsync(cancellationToken).ConfigureAwait(false);
        await _index.StoreFinalSequenceAsync(_nextSequence, _lastSourceSequence, _status.State, _status.Reason, cancellationToken).ConfigureAwait(false);
    }

    private async Task FlushLoopAsync()
    {
        using var timer = new PeriodicTimer(_options.FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    await FlushAsync(_lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    var failure = new StorageFlushException("Periodic storage flush failed.", exception);
                    SetStatus(new RecordingStatus(RecordingState.Stopped, _nextSequence, _lastSourceSequence, failure.Message));
                    await TryStoreStatusAsync(_status).ConfigureAwait(false);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async ValueTask EnsureCapacityAsync(CancellationToken cancellationToken)
    {
        if (_options.MinimumFreeBytes <= 0) return;
        long available;
        try
        {
            available = _options.CapacityProbe.GetAvailableBytes(_options.StorageDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var status = new RecordingStatus(RecordingState.CapacityStopped, _nextSequence, _lastSourceSequence, "Storage capacity could not be determined.");
            SetStatus(status);
            await TryStoreStatusAsync(status).ConfigureAwait(false);
            throw new StorageCapacityException(status.Reason!, exception);
        }

        if (available < _options.MinimumFreeBytes)
        {
            var status = new RecordingStatus(RecordingState.CapacityStopped, _nextSequence, _lastSourceSequence, "Configured free-space reserve reached.");
            SetStatus(status);
            await TryStoreStatusAsync(status).ConfigureAwait(false);
            throw new StorageCapacityException(status.Reason!);
        }
    }

    private void EnsureRunning()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_status.State is not RecordingState.Running) throw new InvalidOperationException($"Recording is not running: {_status.State}.");
    }

    private void SetStatus(RecordingStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(status);
    }

    private async ValueTask TryStoreStatusAsync(RecordingStatus status)
    {
        try
        {
            await _index.StoreFinalSequenceAsync(status.LastSequence, status.LastSourceSequence, status.State, status.Reason, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The append log remains the source of truth if SQLite is unavailable.
        }
    }

    private static bool IsCapacityFailure(Exception exception)
    {
        if (exception is SqliteException sqlite && sqlite.SqliteErrorCode is 13 or 14) return true;
        if (exception is IOException io && unchecked((uint)io.HResult) == 0x80070070) return true;
        return exception.Message.Contains("space", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("disk full", StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveSqliteFiles(string directory)
    {
        foreach (var path in new[] { Path.Combine(directory, "index.sqlite"), Path.Combine(directory, "index.sqlite-wal"), Path.Combine(directory, "index.sqlite-shm") })
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (_status.State == RecordingState.Running) await StopAsync().ConfigureAwait(false);
            else
            {
                _lifetime.Cancel();
                await _flushLoop.ConfigureAwait(false);
                await _segments.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await _index.DisposeAsync().ConfigureAwait(false);
            _lifetime.Dispose();
            _writerGate.Dispose();
        }
    }
}
