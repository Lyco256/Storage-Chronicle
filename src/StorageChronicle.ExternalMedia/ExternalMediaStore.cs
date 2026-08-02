using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.ExternalMedia;

/// <summary>Writes, validates, and reads immutable media-related event segments.</summary>
public sealed class ExternalMediaStore
{
    private const string DirectoryName = ".StorageChronicle";
    private const int MaxRecordBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = false };
    private readonly string mediaRoot;
    private readonly string root;
    private readonly string writerId;
    private readonly IMediaClock clock;

    /// <summary>Initializes a store for one media root and writer PC.</summary>
    public ExternalMediaStore(string mediaRoot, string writerId, IMediaClock? clock = null, bool createIfMissing = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(writerId);
        this.mediaRoot = Path.GetFullPath(mediaRoot);
        this.writerId = ValidateComponent(writerId, nameof(writerId));
        this.clock = clock ?? new SystemMediaClock();
        root = Path.Combine(this.mediaRoot, DirectoryName);
        if (createIfMissing) Directory.CreateDirectory(WriterDirectory);
    }

    /// <summary>Absolute media root supplied to the store.</summary>
    public string MediaRoot => mediaRoot;
    /// <summary>Dedicated log directory which must be excluded from filesystem monitoring.</summary>
    public string MediaLogDirectory => root;
    /// <summary>Independent writer directory for this PC.</summary>
    public string WriterDirectory => Path.Combine(root, "writers", writerId);
    /// <summary>Writer PC identifier.</summary>
    public string WriterPcId => writerId;

    /// <summary>Returns the dedicated log directory and registers it for monitoring exclusion.</summary>
    public string RegisterMonitoringExclusion(IMediaMonitoringExclusionRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        return registrar.Register(root);
    }

    /// <summary>Validates mirror policy before a caller enables media mirroring.</summary>
    public static MediaMirrorConfiguration ValidateMirrorConfiguration(MediaMirrorConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Enabled && !configuration.IsAllowed) throw new InvalidOperationException("System, boot, recovery, and EFI volumes cannot contain a media mirror.");
        if (configuration.Enabled) ArgumentException.ThrowIfNullOrWhiteSpace(configuration.MediaRoot);
        return configuration with { MediaRoot = configuration.Enabled ? Path.GetFullPath(configuration.MediaRoot) : configuration.MediaRoot };
    }

    /// <summary>Appends canonical events to a temporary segment and atomically finalizes it.</summary>
    public async ValueTask<MediaSegment> AppendSegmentAsync(IReadOnlyList<CanonicalEvent> events, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0) throw new ArgumentException("A media segment must contain at least one event.", nameof(events));
        Directory.CreateDirectory(WriterDirectory);
        var id = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(WriterDirectory, id + ".tmp");
        var final = Path.Combine(WriterDirectory, id + ".seg");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                foreach (var value in events)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
                    if (payload.Length > MaxRecordBytes) throw new InvalidDataException("A media record exceeds the bounded segment record size.");
                    var length = new byte[sizeof(int)];
                    BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
                    await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
                    await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                    await stream.WriteAsync(MediaCrc32C.Compute(payload), cancellationToken).ConfigureAwait(false);
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, final);
            var sha = await ComputeSha256Async(final, cancellationToken).ConfigureAwait(false);
            var lengthOnDisk = new FileInfo(final).Length;
            return new MediaSegment(Path.GetFileName(final), sha, events.Count, writerId, lengthOnDisk);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>Reads and verifies one finalized segment, including record CRC32C and segment SHA-256.</summary>
    public async ValueTask<IReadOnlyList<CanonicalEvent>> ReadSegmentAsync(MediaSegment segment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segment);
        EnsureSafeFileName(segment.FileName, ".seg");
        var path = Path.Combine(WriterDirectory, segment.FileName);
        if (!File.Exists(path)) throw new FileNotFoundException("The referenced media segment is missing.", path);
        if (string.IsNullOrWhiteSpace(segment.Sha256) || segment.Sha256.Length != 64 || !segment.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("The media segment SHA-256 is malformed.");
        var actualSha = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actualSha), Convert.FromHexString(segment.Sha256))) throw new InvalidDataException("The media segment SHA-256 does not match its manifest.");

        var events = new List<CanonicalEvent>(segment.RecordCount);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var lengthBuffer = new byte[sizeof(int)];
        while (true)
        {
            var read = await ReadAtMostAsync(stream, lengthBuffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (read != lengthBuffer.Length) throw new InvalidDataException("The media segment ends in a partial record length.");
            var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
            if (length is <= 0 or > MaxRecordBytes) throw new InvalidDataException("The media segment record length is invalid.");
            var payload = new byte[length];
            if (await ReadAtMostAsync(stream, payload, cancellationToken).ConfigureAwait(false) != length) throw new InvalidDataException("The media segment ends in a partial payload.");
            var crc = new byte[sizeof(uint)];
            if (await ReadAtMostAsync(stream, crc, cancellationToken).ConfigureAwait(false) != crc.Length) throw new InvalidDataException("The media segment ends in a partial CRC.");
            if (!CryptographicOperations.FixedTimeEquals(MediaCrc32C.Compute(payload), crc)) throw new InvalidDataException("The media record CRC32C does not match.");
            var value = JsonSerializer.Deserialize<CanonicalEvent>(payload, JsonOptions) ?? throw new InvalidDataException("The media record is empty.");
            events.Add(value);
        }

        if (events.Count != segment.RecordCount) throw new InvalidDataException("The media segment record count does not match its manifest.");
        return events;
    }

    /// <summary>Publishes a self-hashed A/B manifest using an atomic temporary rename.</summary>
    public async ValueTask<MediaManifest> PublishManifestAsync(string logicalMediaId, string? parentManifestSha256, string mountSessionId, IReadOnlyList<MediaSegment> segments, CancellationToken cancellationToken = default)
    {
        ValidateLogicalIdentity(logicalMediaId, mountSessionId);
        ArgumentNullException.ThrowIfNull(segments);
        foreach (var segment in segments)
        {
            EnsureSafeFileName(segment.FileName, ".seg");
            if (!string.Equals(segment.WriterPcId, writerId, StringComparison.Ordinal)) throw new InvalidDataException("A writer can publish only its own segment references.");
        }

        var manifest = new MediaManifest(MediaFormatVersions.Format, MediaFormatVersions.Schema, logicalMediaId, writerId, mountSessionId, parentManifestSha256, segments.ToArray(), clock.UtcNow, null, MediaFormatVersions.Projection);
        var sealedManifest = manifest with { Sha256 = ComputeManifestSha256(manifest) };
        var slot = await SelectWriteSlotAsync(cancellationToken).ConfigureAwait(false);
        var temporary = Path.Combine(WriterDirectory, $"manifest-{slot}.tmp");
        var final = Path.Combine(WriterDirectory, $"manifest-{slot}.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(sealedManifest, JsonOptions);
        await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, final, true);
        return sealedManifest;
    }

    /// <summary>Returns the newest valid A/B manifest after self-hash and path validation.</summary>
    public async ValueTask<MediaManifest?> ReadManifestSlotAsync(CancellationToken cancellationToken = default)
    {
        var candidates = await ReadManifestCandidatesAsync(cancellationToken).ConfigureAwait(false);
        return candidates.OrderByDescending(value => value.CreatedUtc).ThenByDescending(value => value.Sha256, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>Reads every valid manifest from this writer's A/B slots, newest first.</summary>
    public async ValueTask<IReadOnlyList<MediaManifest>> ReadManifestCandidatesAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(root)) return Array.Empty<MediaManifest>();
        var values = new List<MediaManifest>();
        foreach (var slot in new[] { "A", "B" })
        {
            var path = Path.Combine(WriterDirectory, $"manifest-{slot}.json");
            if (!File.Exists(path)) continue;
            try
            {
                var manifest = JsonSerializer.Deserialize<MediaManifest>(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions);
                if (manifest is not null)
                {
                    var validation = ValidateManifest(manifest);
                    if (!validation.IsValid) throw new InvalidDataException(validation.Error);
                    values.Add(manifest);
                }
            }
            catch (JsonException) { }
            catch (InvalidDataException) { }
            catch (FormatException) { }
        }

        return values.OrderByDescending(value => value.CreatedUtc).ThenByDescending(value => value.Sha256, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Validates manifest versions, self SHA-256, safe segment names, and identity fields.</summary>
    public static MediaManifestValidation ValidateManifest(MediaManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.FormatVersion != MediaFormatVersions.Format || manifest.SchemaVersion != MediaFormatVersions.Schema || manifest.ProjectionVersion != MediaFormatVersions.Projection) return new(false, "The media manifest version is unsupported.");
        if (string.IsNullOrWhiteSpace(manifest.LogicalMediaId) || string.IsNullOrWhiteSpace(manifest.WriterPcId) || string.IsNullOrWhiteSpace(manifest.MountSessionId)) return new(false, "The media manifest identity is incomplete.");
        if (manifest.ParentManifestSha256 is not null && (manifest.ParentManifestSha256.Length != 64 || !manifest.ParentManifestSha256.All(Uri.IsHexDigit))) return new(false, "The media manifest parent SHA-256 is malformed.");
        if (string.IsNullOrWhiteSpace(manifest.Sha256)) return new(false, "The media manifest has no self SHA-256.");
        if (manifest.Segments is null) return new(false, "The media manifest has no segment collection.");
        try
        {
            foreach (var segment in manifest.Segments)
            {
                EnsureSafeFileNameStatic(segment.FileName, ".seg");
                if (segment.RecordCount <= 0 || string.IsNullOrWhiteSpace(segment.Sha256) || segment.Sha256.Length != 64 || !segment.Sha256.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(segment.WriterPcId)) return new(false, "The media manifest contains an invalid segment reference.");
            }
            var expected = ComputeManifestSha256(manifest with { Sha256 = null });
            var valid = CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(manifest.Sha256));
            return valid ? new(true, null) : new(false, "The media manifest self SHA-256 does not match.");
        }
        catch (FormatException) { return new(false, "The media manifest contains a malformed SHA-256."); }
        catch (InvalidDataException exception) { return new(false, exception.Message); }
    }

    /// <summary>Detects a history branch only when distinct valid children share one parent.</summary>
    public static HistoryBranchId DetectBranch(IEnumerable<MediaManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        var group = manifests.Where(value => ValidateManifest(value).IsValid && value.ParentManifestSha256 is not null)
            .GroupBy(value => value.ParentManifestSha256!, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(values => values.Select(value => value.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
        return group is null
            ? HistoryBranchId.Create("linear")
            : HistoryBranchId.Create($"branch-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(group.Key)))[..16]}");
    }

    /// <summary>Recovers the single temporary segment left by one interrupted write.</summary>
    public async ValueTask<InterruptedSegmentRecovery?> RecoverInterruptedWriteAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(WriterDirectory)) return null;
        var temporaryFiles = Directory.EnumerateFiles(WriterDirectory, "*.tmp", SearchOption.TopDirectoryOnly).ToArray();
        if (temporaryFiles.Length == 0) return null;
        if (temporaryFiles.Length > 1) throw new InvalidDataException("More than one unfinalized media segment was found; recovery is ambiguous.");
        var temporary = temporaryFiles[0];
        var fileName = Path.GetFileName(temporary);
        try
        {
            var bytes = await File.ReadAllBytesAsync(temporary, cancellationToken).ConfigureAwait(false);
            var valid = TryValidateSegmentBytes(bytes, out var recordCount);
            if (!valid)
            {
                TryDelete(temporary);
                return new InterruptedSegmentRecovery(fileName, false, true, "The incomplete segment was discarded after CRC/record-boundary validation failed.");
            }

            var final = Path.Combine(WriterDirectory, Path.GetFileNameWithoutExtension(fileName) + ".seg");
            File.Move(temporary, final);
            return new InterruptedSegmentRecovery(fileName, true, false, $"The complete temporary segment was finalized with {recordCount} records.");
        }
        catch (EndOfStreamException)
        {
            TryDelete(temporary);
            return new InterruptedSegmentRecovery(fileName, false, true, "The incomplete segment was discarded.");
        }
    }

    private async ValueTask<string> SelectWriteSlotAsync(CancellationToken cancellationToken)
    {
        var current = await ReadManifestSlotAsync(cancellationToken).ConfigureAwait(false);
        return current is null ? "A" : "B";
    }

    private static string ComputeManifestSha256(MediaManifest manifest)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest with { Sha256 = null }, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static async ValueTask<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask<int> ReadAtMostAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }

        return total;
    }

    private static bool TryValidateSegmentBytes(byte[] bytes, out int recordCount)
    {
        recordCount = 0;
        var position = 0;
        while (position < bytes.Length)
        {
            if (bytes.Length - position < sizeof(int)) return false;
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(position, sizeof(int)));
            position += sizeof(int);
            if (length is <= 0 or > MaxRecordBytes || bytes.Length - position < length + sizeof(uint)) return false;
            var payload = bytes.AsSpan(position, length);
            position += length;
            var crc = bytes.AsSpan(position, sizeof(uint));
            position += sizeof(uint);
            if (!CryptographicOperations.FixedTimeEquals(MediaCrc32C.Compute(payload), crc)) return false;
            recordCount++;
        }

        return position == bytes.Length && recordCount > 0;
    }

    private static void ValidateLogicalIdentity(string logicalMediaId, string mountSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalMediaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(mountSessionId);
        ValidateComponent(logicalMediaId, nameof(logicalMediaId));
        ValidateComponent(mountSessionId, nameof(mountSessionId));
    }

    private static string ValidateComponent(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar)) throw new ArgumentException("An identity component cannot contain path separators.", parameterName);
        return value;
    }

    private static void EnsureSafeFileNameStatic(string fileName, string extension)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName) || !fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A media file path is not a safe finalized segment name.");
    }

    private static void EnsureSafeFileName(string fileName, string extension) => EnsureSafeFileNameStatic(fileName, extension);
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } }
}

/// <summary>Result of validating one media manifest.</summary>
public sealed record MediaManifestValidation(bool IsValid, string? Error);

/// <summary>Describes one immutable media segment.</summary>
public sealed record MediaSegment(string FileName, string Sha256, int RecordCount, string WriterPcId, long ByteLength = 0);

/// <summary>Describes a complete, self-hashed manifest and its immutable parent reference.</summary>
public sealed record MediaManifest(
    string FormatVersion,
    EventSchemaVersion SchemaVersion,
    string LogicalMediaId,
    string WriterPcId,
    string MountSessionId,
    string? ParentManifestSha256,
    IReadOnlyList<MediaSegment> Segments,
    DateTimeOffset CreatedUtc,
    string? Sha256 = null,
    string ProjectionVersion = MediaFormatVersions.Projection);

/// <summary>Tracks media mount sessions without correcting recorded clock values.</summary>
public sealed class MountSessionTracker
{
    private readonly IMediaClock clock;
    private MountSession? previous;

    /// <summary>Initializes a tracker with the system clock.</summary>
    public MountSessionTracker(IMediaClock? clock = null) => this.clock = clock ?? new SystemMediaClock();

    /// <summary>Starts a session linked to the previous session.</summary>
    public MountSession Start(VolumeId volumeId, string pcId, MonitoringContinuity continuity)
    {
        var session = new MountSession(MountSessionId.Create(Guid.NewGuid().ToString("N")), volumeId, pcId, clock.UtcNow, null, previous?.Id, continuity, ImmutableArray<string>.Empty);
        previous = session;
        return session;
    }
}

internal static class MediaCrc32C
{
    private const uint Polynomial = 0x82F63B78;

    public static byte[] Compute(ReadOnlySpan<byte> value)
    {
        uint crc = uint.MaxValue;
        foreach (var item in value)
        {
            crc ^= item;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (Polynomial & (uint)-(int)(crc & 1));
        }

        return BitConverter.GetBytes(~crc);
    }
}
