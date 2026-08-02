using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZstdSharp;

namespace StorageChronicle.Storage;

internal sealed record SegmentRecord(StorageRecordKind Kind, int SchemaMajor, int SchemaMinor, long Sequence, byte[] Payload);

internal sealed class SegmentLog : IAsyncDisposable
{
    private const int FormatVersion = 1;
    private const int HeaderSize = 32;
    private const int MinimumFrameSize = 25;
    private static readonly byte[] Magic = "SCE1"u8.ToArray();
    private readonly StorageEngineOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FileStream? _currentStream;
    private Guid _currentId;
    private long _currentFirstSequence;
    private long _currentLastSequence;
    private int _currentRecordCount;

    internal SegmentLog(StorageEngineOptions options)
    {
        _options = options;
        Directory.CreateDirectory(options.StorageDirectory);
    }

    internal event Action<SegmentIssue>? SegmentSkipped;

    internal async ValueTask<SegmentRecord> AppendAsync(StorageRecordKind kind, int schemaMajor, int schemaMinor, long sequence, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length > int.MaxValue - MinimumFrameSize) throw new ArgumentOutOfRangeException(nameof(payload));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var frame = BuildFrame(kind, schemaMajor, schemaMinor, sequence, payload);
            await EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (_currentStream!.Length > HeaderSize && _currentStream.Length + frame.Length > _options.SegmentMaxBytes)
            {
                await CloseCurrentAsync(cancellationToken).ConfigureAwait(false);
                await EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
            }

            await _currentStream!.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            _currentFirstSequence = _currentRecordCount == 0 ? sequence : _currentFirstSequence;
            _currentLastSequence = sequence;
            _currentRecordCount++;
            return new SegmentRecord(kind, schemaMajor, schemaMinor, sequence, payload);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_currentStream is not null)
            {
                await _currentStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                _currentStream.Flush(flushToDisk: true);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseCurrentAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask<IReadOnlyList<SegmentInfo>> EnumerateHealthySegmentsAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return ReadHealthySegments().Select(static segment => segment.Info).ToArray();
    }

    internal IEnumerable<SegmentRecord> ReadAllRecords()
    {
        foreach (var segment in ReadHealthySegments().OrderBy(static value => value.Info.FirstSequence))
        {
            foreach (var record in segment.Records)
            {
                yield return record;
            }
        }
    }

    private async ValueTask EnsureCurrentAsync(CancellationToken cancellationToken)
    {
        if (_currentStream is not null) return;

        var candidates = ReadHealthySegments()
            .Where(static segment => !segment.Info.IsCompressed && segment.Info.Path.EndsWith(".open", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static segment => segment.Info.LastSequence)
            .ToArray();
        if (candidates.Length > 0)
        {
            var selected = candidates[0];
            _currentId = selected.Info.SegmentId;
            _currentFirstSequence = selected.Info.FirstSequence;
            _currentLastSequence = selected.Info.LastSequence;
            _currentRecordCount = selected.Info.RecordCount;
            _currentStream = new FileStream(selected.Info.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            _currentStream.Seek(0, SeekOrigin.End);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _currentId = Guid.NewGuid();
        _currentFirstSequence = 0;
        _currentLastSequence = 0;
        _currentRecordCount = 0;
        var path = OpenPath(_currentId);
        _currentStream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = BuildHeader(_currentId, DateTimeOffset.UtcNow.UtcTicks);
        await _currentStream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await _currentStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask CloseCurrentAsync(CancellationToken cancellationToken)
    {
        if (_currentStream is null) return;
        await _currentStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        _currentStream.Flush(flushToDisk: true);
        var path = _currentStream.Name;
        await _currentStream.DisposeAsync().ConfigureAwait(false);
        _currentStream = null;
        if (_currentRecordCount == 0)
        {
            File.Delete(path);
            return;
        }

        var raw = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var parsed = ParseRaw(raw, path, allowTailTruncation: false);
        var finalPath = path;
        var compressed = false;
        if (_options.CompressClosedSegments)
        {
            var compressedBytes = Compress(raw);
            var roundTrip = Decompress(compressedBytes);
            if (!raw.AsSpan().SequenceEqual(roundTrip)) throw new StorageFlushException($"Closed segment verification failed: {path}");
            finalPath = CompressedPath(_currentId);
            var temporaryPath = finalPath + ".tmp";
            await File.WriteAllBytesAsync(temporaryPath, compressedBytes, cancellationToken).ConfigureAwait(false);
            using (var temporary = new FileStream(temporaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
            {
                temporary.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, finalPath, overwrite: true);
            File.Delete(path);
            compressed = true;
        }

        var info = CreateInfo(_currentId, finalPath, compressed, parsed.Records);
        await WriteManifestAsync(info, cancellationToken).ConfigureAwait(false);
        _currentId = Guid.Empty;
        _currentFirstSequence = 0;
        _currentLastSequence = 0;
        _currentRecordCount = 0;
    }

    private List<ParsedSegment> ReadHealthySegments()
    {
        var paths = Directory.EnumerateFiles(_options.StorageDirectory, "segment-*")
            .Where(static path => path.EndsWith(".open", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase);
        var result = new List<ParsedSegment>();
        foreach (var path in paths)
        {
            try
            {
                var compressed = path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase);
                var bytes = File.ReadAllBytes(path);
                var raw = compressed ? Decompress(bytes) : bytes;
                var parsed = ParseRaw(raw, path, allowTailTruncation: !compressed);
                var id = parsed.SegmentId;
                var info = CreateInfo(id, path, compressed, parsed.Records);
                result.Add(new ParsedSegment(info, parsed.Records));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or FormatException)
            {
                SegmentSkipped?.Invoke(new SegmentIssue(path, exception.Message));
            }
        }

        return result;
    }

    private async ValueTask WriteManifestAsync(SegmentInfo info, CancellationToken cancellationToken)
    {
        var manifest = new
        {
            formatVersion = FormatVersion,
            segmentId = info.SegmentId,
            firstSequence = info.FirstSequence,
            lastSequence = info.LastSequence,
            recordCount = info.RecordCount,
            compressed = info.IsCompressed,
            historyBranch = info.HistoryBranch,
            reference = info.ManifestReference
        };
        var path = ManifestPath(info.SegmentId);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await JsonSerializer.SerializeAsync(stream, manifest, cancellationToken: cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private SegmentInfo CreateInfo(Guid id, string path, bool compressed, IReadOnlyList<SegmentRecord> records)
    {
        var first = records.Count == 0 ? 0 : records[0].Sequence;
        var last = records.Count == 0 ? 0 : records[^1].Sequence;
        var metadata = $"{FormatVersion}|{id:N}|{first}|{last}|{records.Count}|{compressed}|{_options.HistoryBranch}";
        var reference = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(metadata)));
        return new SegmentInfo(id, path, compressed, first, last, records.Count, reference, _options.HistoryBranch);
    }

    private static byte[] BuildHeader(Guid id, long createdTicks)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), FormatVersion);
        id.TryWriteBytes(header.AsSpan(8, 16));
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24), createdTicks);
        return header;
    }

    private static byte[] BuildFrame(StorageRecordKind kind, int schemaMajor, int schemaMinor, long sequence, byte[] payload)
    {
        var frameLength = MinimumFrameSize + payload.Length;
        var frame = new byte[sizeof(int) + frameLength];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0), frameLength);
        frame[4] = (byte)kind;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(5), schemaMajor);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(9), schemaMinor);
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(13), sequence);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(21), payload.Length);
        payload.CopyTo(frame.AsSpan(25));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(25 + payload.Length), Crc32C.Compute(frame.AsSpan(4, 21 + payload.Length)));
        return frame;
    }

    private static ParsedRawSegment ParseRaw(byte[] bytes, string path, bool allowTailTruncation)
    {
        if (bytes.Length < HeaderSize)
        {
            if (allowTailTruncation)
            {
                File.WriteAllBytes(path, BuildHeader(Guid.NewGuid(), DateTimeOffset.UtcNow.UtcTicks));
                bytes = File.ReadAllBytes(path);
            }
            else throw new InvalidDataException("Segment header is incomplete.");
        }

        if (!bytes.AsSpan(0, 4).SequenceEqual(Magic)) throw new InvalidDataException("Segment magic is invalid.");
        if (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4)) != FormatVersion) throw new InvalidDataException("Segment format version is unsupported.");
        var id = new Guid(bytes.AsSpan(8, 16));
        var records = new List<SegmentRecord>();
        var offset = HeaderSize;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < sizeof(int))
            {
                if (!allowTailTruncation) throw new InvalidDataException("Segment has an incomplete record length.");
                File.WriteAllBytes(path, bytes.AsSpan(0, offset).ToArray());
                break;
            }

            var frameLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, sizeof(int)));
            if (frameLength < MinimumFrameSize || frameLength > bytes.Length - offset - sizeof(int))
            {
                if (frameLength >= MinimumFrameSize && allowTailTruncation)
                {
                    File.WriteAllBytes(path, bytes.AsSpan(0, offset).ToArray());
                    break;
                }

                throw new InvalidDataException("Segment record length is invalid.");
            }

            var frame = bytes.AsSpan(offset + sizeof(int), frameLength);
            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(17, sizeof(int)));
            if (payloadLength < 0 || payloadLength != frameLength - MinimumFrameSize) throw new InvalidDataException("Segment payload length is invalid.");
            var expected = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(frameLength - sizeof(uint), sizeof(uint)));
            var actual = Crc32C.Compute(frame[..(frameLength - sizeof(uint))]);
            if (expected != actual) throw new InvalidDataException("Segment CRC32C does not match.");
            var kind = (StorageRecordKind)frame[0];
            if (!Enum.IsDefined(kind)) throw new InvalidDataException("Segment record kind is invalid.");
            var schemaMajor = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(1, sizeof(int)));
            var schemaMinor = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(5, sizeof(int)));
            var sequence = BinaryPrimitives.ReadInt64LittleEndian(frame.Slice(9, sizeof(long)));
            records.Add(new SegmentRecord(kind, schemaMajor, schemaMinor, sequence, frame.Slice(21, payloadLength).ToArray()));
            offset += sizeof(int) + frameLength;
        }

        return new ParsedRawSegment(id, records);
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var compressor = new Compressor(3);
        return compressor.Wrap(bytes).ToArray();
    }

    private static byte[] Decompress(byte[] bytes)
    {
        using var decompressor = new Decompressor();
        return decompressor.Unwrap(bytes).ToArray();
    }

    private string OpenPath(Guid id) => Path.Combine(_options.StorageDirectory, $"segment-{id:N}.open");
    private string CompressedPath(Guid id) => Path.Combine(_options.StorageDirectory, $"segment-{id:N}.zst");
    private string ManifestPath(Guid id) => Path.Combine(_options.StorageDirectory, $"segment-{id:N}.manifest.json");

    public async ValueTask DisposeAsync()
    {
        await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Dispose();
    }

    private sealed record ParsedRawSegment(Guid SegmentId, IReadOnlyList<SegmentRecord> Records);
    private sealed record ParsedSegment(SegmentInfo Info, IReadOnlyList<SegmentRecord> Records);
}
