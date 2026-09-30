using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;

[assembly: InternalsVisibleTo("StorageChronicle.ExternalMedia.Tests")]
[assembly: InternalsVisibleTo("StorageChronicle.Agent.Tests")]

namespace StorageChronicle.ExternalMedia;

/// <summary>Durable, PC-local proof that one exact temporary media segment was created by this product.</summary>
internal sealed record MediaRecoveryIntent(VolumeId VolumeId, string WriterPcId, string TemporaryFileName, long Length, string Sha256);

/// <summary>Stores and removes recovery intents outside the removable media on a verified product-owned PC path.</summary>
/// <remarks>Implementations must be durable, append-safe, and must not fall back to paths on the media volume.</remarks>
internal interface IMediaRecoveryIntentStore
{
    /// <summary>Durably records a new intent without replacing an existing intent.</summary>
    ValueTask SaveAsync(MediaRecoveryIntent intent, CancellationToken cancellationToken = default);

    /// <summary>Reads the intent for one exact temporary name, or returns null when no intent exists.</summary>
    ValueTask<MediaRecoveryIntent?> FindAsync(VolumeId volumeId, string writerPcId, string temporaryFileName, CancellationToken cancellationToken = default);

    /// <summary>Removes an intent only after its temporary was safely finalized.</summary>
    ValueTask RemoveAsync(VolumeId volumeId, string writerPcId, string temporaryFileName, CancellationToken cancellationToken = default);
}

/// <summary>Stores recovery proofs in the fixed per-PC Storage Chronicle product-data area.</summary>
internal sealed class ProductMediaRecoveryIntentStore : IMediaRecoveryIntentStore
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false };
    private readonly string directory;

    /// <summary>Initializes the store beneath CommonApplicationData; no caller-supplied path is accepted.</summary>
    public ProductMediaRecoveryIntentStore()
    {
        directory = GetIntentDirectory(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
    }

    internal ProductMediaRecoveryIntentStore(string isolatedFixtureRoot, bool isolatedTestRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isolatedFixtureRoot);
        if (!isolatedTestRoot) throw new ArgumentException("Only a run-owned isolated test root is accepted.", nameof(isolatedTestRoot));
        var fullFixtureRoot = Path.GetFullPath(isolatedFixtureRoot);
        var testParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "StorageChronicle.Media.Tests"));
        var prefix = Path.TrimEndingDirectorySeparator(testParent) + Path.DirectorySeparatorChar;
        if (!fullFixtureRoot.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !Guid.TryParseExact(Path.GetFileName(fullFixtureRoot), "N", out _) || !File.Exists(Path.Combine(fullFixtureRoot, ".test-owner.json")))
            throw new ArgumentException("Recovery-intent test storage requires a run-owned fixture under the dedicated temp parent.", nameof(isolatedFixtureRoot));
        directory = GetIntentDirectory(fullFixtureRoot);
    }

    internal static void ValidateCommonDataRoot(string commonData)
    {
        if (string.IsNullOrWhiteSpace(commonData) || !Path.IsPathFullyQualified(commonData) || commonData.StartsWith("\\\\", StringComparison.Ordinal) || commonData.StartsWith("//", StringComparison.Ordinal))
            throw new IOException("The fixed PC-local product data root could not be established.");
        var fullCommonData = Path.GetFullPath(commonData);
        if (!string.Equals(fullCommonData, Path.TrimEndingDirectorySeparator(commonData), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("CommonApplicationData is not a normalized absolute local path.");
        if (new DriveInfo(Path.GetPathRoot(fullCommonData)!).DriveType != DriveType.Fixed)
            throw new IOException("CommonApplicationData is not on a fixed local drive.");
        EnsureNoReparsePoints(fullCommonData);
    }

    private static string GetIntentDirectory(string commonData)
    {
        ValidateCommonDataRoot(commonData);
        var fullCommonData = Path.GetFullPath(commonData);
        var intentDirectory = Path.GetFullPath(Path.Combine(fullCommonData, "Storage Chronicle", "history", "media-recovery-intents"));
        if (!IsContainedBy(intentDirectory, fullCommonData)) throw new IOException("The PC-local recovery-intent path escaped CommonApplicationData.");
        return intentDirectory;
    }

    /// <inheritdoc />
    public async ValueTask SaveAsync(MediaRecoveryIntent intent, CancellationToken cancellationToken = default)
    {
        ValidateIntent(intent);
        EnsureDirectory(createIfMissing: true);
        var destination = GetIntentPath(intent.VolumeId, intent.WriterPcId, intent.TemporaryFileName);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(intent, Options);
        if (File.Exists(destination))
        {
            var existing = await ReadAsync(destination, cancellationToken).ConfigureAwait(false);
            if (existing == intent) return;
            throw new IOException("A different recovery intent already occupies the unique product-local intent name.");
        }

        var temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        var ownsTemporary = false;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                ownsTemporary = true;
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporary, destination, overwrite: false);
                ownsTemporary = false;
            }
            catch (IOException) when (File.Exists(destination))
            {
                var existing = await ReadAsync(destination, cancellationToken).ConfigureAwait(false);
                if (existing != intent) throw new IOException("A different recovery intent already occupies the unique product-local intent name.");
            }
        }
        catch
        {
            if (ownsTemporary)
            {
                try { EnsureNoReparsePoints(temporary); File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }

        if (ownsTemporary)
        {
            try { EnsureNoReparsePoints(temporary); File.Delete(temporary); }
            catch (FileNotFoundException) { }
        }
    }

    /// <inheritdoc />
    public async ValueTask<MediaRecoveryIntent?> FindAsync(VolumeId volumeId, string writerPcId, string temporaryFileName, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(volumeId, writerPcId, temporaryFileName);
        EnsureDirectory(createIfMissing: false);
        var path = GetIntentPath(volumeId, writerPcId, temporaryFileName);
        if (!File.Exists(path)) return null;
        var intent = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        ValidateIntent(intent);
        return intent.VolumeId == volumeId && intent.WriterPcId == writerPcId && intent.TemporaryFileName == temporaryFileName ? intent : null;
    }

    /// <inheritdoc />
    public async ValueTask RemoveAsync(VolumeId volumeId, string writerPcId, string temporaryFileName, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(volumeId, writerPcId, temporaryFileName);
        EnsureDirectory(createIfMissing: false);
        var path = GetIntentPath(volumeId, writerPcId, temporaryFileName);
        if (!File.Exists(path)) return;
        var intent = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        ValidateIntent(intent);
        if (intent.VolumeId != volumeId || intent.WriterPcId != writerPcId || intent.TemporaryFileName != temporaryFileName)
            throw new IOException("The product-local recovery intent identity changed; it was preserved.");
        File.Delete(path);
    }

    private void EnsureDirectory(bool createIfMissing)
    {
        EnsureNoReparsePoints(Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)));
        if (!Directory.Exists(directory))
        {
            if (!createIfMissing) return;
            EnsureNoReparsePoints(directory);
            Directory.CreateDirectory(directory);
        }
        EnsureNoReparsePoints(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("A reparse point exists in the product-local recovery-intent directory.");
            var name = Path.GetFileName(entry);
            var intentName = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && Path.GetFileNameWithoutExtension(name).Length == 64 && Path.GetFileNameWithoutExtension(name).All(Uri.IsHexDigit);
            var temporaryName = name.StartsWith('.') && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(name[1..^4], "N", out _);
            if (Directory.Exists(entry) || (!intentName && !temporaryName))
                throw new IOException("An unrecognized entry exists in the product-local recovery-intent directory; no entry was changed.");
        }
    }

    private string GetIntentPath(VolumeId volumeId, string writerPcId, string temporaryFileName)
    {
        var identity = string.Join("\n", volumeId.Value, writerPcId, temporaryFileName);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(directory, key + ".json");
    }

    private static async ValueTask<MediaRecoveryIntent> ReadAsync(string path, CancellationToken cancellationToken)
    {
        EnsureNoReparsePoints(path);
        try
        {
            var intent = JsonSerializer.Deserialize<MediaRecoveryIntent>(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), Options);
            return intent ?? throw new InvalidDataException("The product-local recovery intent is empty.");
        }
        catch (JsonException exception) { throw new InvalidDataException("The product-local recovery intent is malformed and was preserved.", exception); }
    }

    private static void ValidateIntent(MediaRecoveryIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ValidateIdentity(intent.VolumeId, intent.WriterPcId, intent.TemporaryFileName);
        if (intent.Length <= 0 || string.IsNullOrWhiteSpace(intent.Sha256) || intent.Sha256.Length != 64 || !intent.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The product-local recovery intent has invalid length or hash fields.");
    }

    private static void ValidateIdentity(VolumeId volumeId, string writerPcId, string temporaryFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeId.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(writerPcId);
        if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(temporaryFileName), "N", out _) || !string.Equals(Path.GetExtension(temporaryFileName), ".tmp", StringComparison.OrdinalIgnoreCase) || temporaryFileName != Path.GetFileName(temporaryFileName))
            throw new InvalidDataException("A recovery intent must identify one GUID-named segment temporary.");
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Product-local recovery-intent paths may not traverse reparse points.");
            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }
    }

    private static bool IsContainedBy(string path, string parent)
    {
        var prefix = Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}

/// <summary>Writes, validates, and reads immutable media-related event segments.</summary>
public sealed class ExternalMediaStore : IDisposable
{
    private const string DirectoryName = ".StorageChronicle";
    private const string OwnershipMarkerName = ".storage-chronicle-owner.json";
    private const string WriterMarkerName = ".writer-owner.json";
    private const string OwnershipSchema = "StorageChronicle.MediaOwnership.v1";
    private const int MaxRecordBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = false };
    private readonly string mediaRoot;
    private readonly string root;
    private readonly string writerId;
    private readonly IMediaClock clock;
    private readonly IVolumeBoundMediaFileSystem fileSystem;
    private readonly IMediaRecoveryIntentStore recoveryIntents;
    private readonly bool ownsFileSystem;

    /// <summary>Initializes a store over an already opened, identity-verified media filesystem session.</summary>
    /// <param name="mediaRoot">Current mount point, used for UI and monitoring-exclusion reporting only.</param>
    /// <param name="writerId">Writer PC identifier.</param>
    /// <param name="expectedVolumeId">Identity required for all file operations in the supplied session.</param>
    /// <param name="fileSystem">Pinned volume filesystem session; path-based fallbacks are not accepted.</param>
    /// <param name="clock">Optional clock for deterministic manifest timestamps.</param>
    /// <param name="createIfMissing">Whether a missing, exclusively created owned layout should be initialized.</param>
    public ExternalMediaStore(string mediaRoot, string writerId, VolumeId expectedVolumeId, IVolumeBoundMediaFileSystem fileSystem, IMediaClock? clock = null, bool createIfMissing = true)
        : this(mediaRoot, writerId, expectedVolumeId, fileSystem, clock, createIfMissing, new ProductMediaRecoveryIntentStore(), ownsFileSystem: true)
    {
    }

    internal ExternalMediaStore(string mediaRoot, string writerId, VolumeId expectedVolumeId, IVolumeBoundMediaFileSystem fileSystem, IMediaClock? clock, bool createIfMissing, IMediaRecoveryIntentStore recoveryIntents)
        : this(mediaRoot, writerId, expectedVolumeId, fileSystem, clock, createIfMissing, recoveryIntents, ownsFileSystem: true)
    {
    }

    private ExternalMediaStore(string mediaRoot, string writerId, VolumeId expectedVolumeId, IVolumeBoundMediaFileSystem fileSystem, IMediaClock? clock, bool createIfMissing, IMediaRecoveryIntentStore recoveryIntents, bool ownsFileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(writerId);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(recoveryIntents);
        if (fileSystem.VolumeId != expectedVolumeId)
        {
            if (ownsFileSystem) fileSystem.Dispose();
            throw new IOException("The media filesystem session is bound to a different volume identity.");
        }
        this.mediaRoot = Path.GetFullPath(mediaRoot);
        this.writerId = ValidateComponent(writerId, nameof(writerId));
        this.clock = clock ?? new SystemMediaClock();
        this.fileSystem = fileSystem;
        this.recoveryIntents = recoveryIntents;
        this.ownsFileSystem = ownsFileSystem;
        root = Path.Combine(this.mediaRoot, DirectoryName);
        try
        {
            if (createIfMissing) EnsureOwnedLayout();
            else ValidateExistingOwnedLayout();
        }
        catch
        {
            if (ownsFileSystem) fileSystem.Dispose();
            throw;
        }
    }

    /// <summary>Disposes the bound filesystem session owned by this store.</summary>
    public void Dispose()
    {
        if (ownsFileSystem) fileSystem.Dispose();
    }

    /// <summary>Absolute media root supplied to the store.</summary>
    public string MediaRoot => mediaRoot;
    /// <summary>Dedicated log directory which must be excluded from filesystem monitoring.</summary>
    public string MediaLogDirectory => root;
    /// <summary>Independent writer directory for this PC.</summary>
    public string WriterDirectory => Path.Combine(root, "writers", writerId);
    /// <summary>Writer PC identifier.</summary>
    public string WriterPcId => writerId;

    internal IReadOnlyList<ExternalMediaStore> OpenWriterStores()
    {
        var writersRoot = Path.Combine(DirectoryName, "writers");
        if (!fileSystem.DirectoryExists(writersRoot)) return Array.Empty<ExternalMediaStore>();
        var stores = new List<ExternalMediaStore>();
        foreach (var entry in fileSystem.EnumerateEntries(writersRoot))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"A media writer entry is a reparse point: {entry.Name}");
            if ((entry.Attributes & FileAttributes.Directory) == 0) throw new IOException($"An unexpected file exists in the media writers directory: {entry.Name}");
            var writer = ValidateComponent(entry.Name, nameof(entry.Name));
            stores.Add(new ExternalMediaStore(mediaRoot, writer, fileSystem.VolumeId, fileSystem, clock, createIfMissing: false, recoveryIntents, ownsFileSystem: false));
        }

        return stores;
    }

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
        if (configuration.Enabled && !configuration.IsAllowed) throw new InvalidOperationException("A media mirror requires complete protected-volume role classification, a write-time volume identity binding, and a non-protected target volume.");
        if (configuration.Enabled) ArgumentException.ThrowIfNullOrWhiteSpace(configuration.MediaRoot);
        return configuration with { MediaRoot = configuration.Enabled ? Path.GetFullPath(configuration.MediaRoot) : configuration.MediaRoot };
    }

    /// <summary>Requires an opted-in mirror root to exactly match a mount point of the connected volume.</summary>
    public static string ValidateMediaRoot(string mediaRoot, IReadOnlyList<string> mountPoints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaRoot);
        ArgumentNullException.ThrowIfNull(mountPoints);
        if (mediaRoot.StartsWith("\\\\", StringComparison.Ordinal) || mediaRoot.StartsWith("//", StringComparison.Ordinal)) throw new InvalidOperationException("External-media history cannot be written to a network or UNC path.");
        if (mountPoints.Count == 0) throw new InvalidOperationException("The connected media has no verified mount point; refusing to use its mirror configuration.");
        var fullRoot = NormalizePath(mediaRoot);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!mountPoints.Any(point => string.Equals(NormalizePath(point), fullRoot, comparison)))
        {
            throw new InvalidOperationException("The configured media mirror root must exactly match a current mount point of the connected media.");
        }

        return fullRoot;
    }

    /// <summary>Appends canonical events to a temporary segment and atomically finalizes it.</summary>
    public async ValueTask<MediaSegment> AppendSegmentAsync(IReadOnlyList<CanonicalEvent> events, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0) throw new ArgumentException("A media segment must contain at least one event.", nameof(events));
        var mediaFilter = CreateBatchMediaFilter(events[0]);
        if (events.Any(value => !MediaEventFilter.IsRelated(value, mediaFilter)))
            throw new InvalidDataException("A media segment can contain only events related to its selected media identity.");
        fileSystem.EnsureDirectory(WriterRelativeDirectory);
        var id = Guid.NewGuid().ToString("N");
        var temporary = RelativeWriterPath(id + ".tmp");
        var final = RelativeWriterPath(id + ".seg");
        using var segmentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long lengthOnDisk = 0;
        await using (var stream = fileSystem.CreateNew(temporary))
        {
            foreach (var value in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
                if (payload.Length > MaxRecordBytes) throw new InvalidDataException("A media record exceeds the bounded segment record size.");
                var length = new byte[sizeof(int)];
                var crc = MediaCrc32C.Compute(payload);
                BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
                segmentHash.AppendData(length);
                segmentHash.AppendData(payload);
                segmentHash.AppendData(crc);
                lengthOnDisk += length.Length + payload.Length + crc.Length;
                await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(crc, cancellationToken).ConfigureAwait(false);
            }

            await FlushDurablyAsync(stream, cancellationToken).ConfigureAwait(false);
            var segmentSha256 = Convert.ToHexString(segmentHash.GetHashAndReset());
            var proof = new MediaRecoveryIntent(fileSystem.VolumeId, writerId, id + ".tmp", lengthOnDisk, segmentSha256);
            await recoveryIntents.SaveAsync(proof, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            fileSystem.MoveCreatedFile(stream, final);
            await RemoveFinalizedIntentAsync(id + ".tmp").ConfigureAwait(false);
            return new MediaSegment(id + ".seg", segmentSha256, events.Count, writerId, lengthOnDisk);
        }
    }

    /// <summary>Reads and verifies one finalized segment, including record CRC32C and segment SHA-256.</summary>
    public async ValueTask<IReadOnlyList<CanonicalEvent>> ReadSegmentAsync(MediaSegment segment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segment);
        EnsureSafeFileName(segment.FileName, ".seg");
        var path = RelativeWriterPath(segment.FileName);
        if (!fileSystem.FileExists(path)) throw new FileNotFoundException("The referenced media segment is missing.", segment.FileName);
        if (string.IsNullOrWhiteSpace(segment.Sha256) || segment.Sha256.Length != 64 || !segment.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("The media segment SHA-256 is malformed.");
        var events = new List<CanonicalEvent>(segment.RecordCount);
        await using var stream = fileSystem.OpenRead(path);
        var actualSha = await ComputeSha256Async(stream, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actualSha), Convert.FromHexString(segment.Sha256))) throw new InvalidDataException("The media segment SHA-256 does not match its manifest.");
        if (!stream.CanSeek) throw new IOException("The bound media stream is not seekable and cannot be verified consistently.");
        stream.Position = 0;
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
        var generation = Guid.NewGuid().ToString("N");
        var temporary = RelativeWriterPath($"manifest-{slot}-{generation}.tmp");
        var final = RelativeWriterPath($"manifest-{slot}-{generation}.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(sealedManifest, JsonOptions);
        await using (var stream = fileSystem.CreateNew(temporary))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await FlushDurablyAsync(stream, cancellationToken).ConfigureAwait(false);
            fileSystem.MoveCreatedFile(stream, final);
        }
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
        if (!fileSystem.DirectoryExists(WriterRelativeDirectory)) return Array.Empty<MediaManifest>();
        var values = new List<MediaManifest>();
        foreach (var entry in fileSystem.EnumerateEntries(WriterRelativeDirectory))
        {
            if ((entry.Attributes & FileAttributes.Directory) != 0 || (entry.Attributes & FileAttributes.ReparsePoint) != 0 || !TryGetManifestSlot(entry.Name, out _)) continue;
            var path = RelativeWriterPath(entry.Name);
            try
            {
                await using var stream = fileSystem.OpenRead(path);
                var manifest = JsonSerializer.Deserialize<MediaManifest>(stream, JsonOptions);
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
        cancellationToken.ThrowIfCancellationRequested();
        if (!fileSystem.DirectoryExists(WriterRelativeDirectory)) return null;
        var temporaryFiles = fileSystem.EnumerateEntries(WriterRelativeDirectory)
            .Where(entry => (entry.Attributes & FileAttributes.Directory) == 0 && entry.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Name)
            .Where(name => !IsManifestGenerationFile(name))
            .ToArray();
        if (temporaryFiles.Length == 0) return null;
        if (temporaryFiles.Length > 1) throw new InvalidDataException("More than one unfinalized media segment was found; recovery is ambiguous.");
        var fileName = temporaryFiles[0];
        if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(fileName), "N", out _) || !string.Equals(Path.GetExtension(fileName), ".tmp", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("An unrecognized temporary file exists in the owned writer directory; it was retained without modification.");
        }

        var temporary = RelativeWriterPath(fileName);
        var intent = await recoveryIntents.FindAsync(fileSystem.VolumeId, writerId, fileName, cancellationToken).ConfigureAwait(false);
        if (intent is null || intent.VolumeId != fileSystem.VolumeId || !string.Equals(intent.WriterPcId, writerId, StringComparison.Ordinal) ||
            !string.Equals(intent.TemporaryFileName, fileName, StringComparison.Ordinal) || intent.Length <= 0 || string.IsNullOrWhiteSpace(intent.Sha256) || intent.Sha256.Length != 64 || !intent.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("The temporary segment was retained unchanged because its PC-local volume/writer/name-bound recovery intent is missing or invalid.");
        }

        await using var input = fileSystem.OpenTemporaryForRecovery(temporary);
        if (input.Length != intent.Length) throw new InvalidDataException("The temporary segment length does not match its PC-local recovery intent; it was retained unchanged.");
        byte[] bytes;
        using (var buffer = new MemoryStream())
        {
            await input.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }
        var valid = TryValidateSegmentBytes(bytes, out var recordCount);
        var actualSha = Convert.ToHexString(SHA256.HashData(bytes));
        if (!valid || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actualSha), Convert.FromHexString(intent.Sha256)))
        {
            throw new InvalidDataException("The temporary media segment was retained unchanged because its CRC/record boundary or PC-local hash intent did not validate.");
        }

        var final = RelativeWriterPath(Path.GetFileNameWithoutExtension(fileName) + ".seg");
        if (fileSystem.FileExists(final)) throw new IOException("The finalized segment destination already exists; neither existing file was changed.");
        cancellationToken.ThrowIfCancellationRequested();
        fileSystem.MoveCreatedFile(input, final);
        await RemoveFinalizedIntentAsync(fileName).ConfigureAwait(false);
        return new InterruptedSegmentRecovery(fileName, true, false, $"The complete temporary segment was finalized with {recordCount} records.");
    }

    private async ValueTask RemoveFinalizedIntentAsync(string temporaryFileName)
    {
        try { await recoveryIntents.RemoveAsync(fileSystem.VolumeId, writerId, temporaryFileName, CancellationToken.None).ConfigureAwait(false); }
        catch (IOException)
        {
            // The segment is already finalized; a stale PC-local intent cannot authorize another rename because its temp is gone.
        }
        catch (UnauthorizedAccessException)
        {
            // Product-local cleanup permissions cannot undo a finalized media segment.
        }
    }

    private async ValueTask<string> SelectWriteSlotAsync(CancellationToken cancellationToken)
    {
        var valid = new List<(string Slot, MediaManifest Manifest)>();
        foreach (var entry in fileSystem.EnumerateEntries(WriterRelativeDirectory))
        {
            if (!TryGetManifestSlot(entry.Name, out var slot)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = fileSystem.OpenRead(RelativeWriterPath(entry.Name));
                var manifest = JsonSerializer.Deserialize<MediaManifest>(stream, JsonOptions);
                if (manifest is not null && ValidateManifest(manifest).IsValid) valid.Add((slot, manifest));
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or FormatException) { }
        }

        var newest = valid.OrderByDescending(value => value.Manifest.CreatedUtc).ThenByDescending(value => value.Manifest.Sha256, StringComparer.Ordinal).FirstOrDefault();
        return newest.Manifest is null || newest.Slot == "B" ? "A" : "B";
    }

    private static string ComputeManifestSha256(MediaManifest manifest)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest with { Sha256 = null }, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static async ValueTask<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken)
    {
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask FlushDurablyAsync(Stream stream, CancellationToken cancellationToken)
    {
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (stream is FileStream fileStream) fileStream.Flush(flushToDisk: true);
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

    private MediaOnlyFilter CreateBatchMediaFilter(CanonicalEvent first)
    {
        ArgumentNullException.ThrowIfNull(first);
        first.Properties.TryGetValue("media.logicalMediaId", out var logicalMediaId);
        var filter = new MediaOnlyFilter(first.VolumeId, logicalMediaId, first.MountSessionId);
        if (!filter.IsSpecified || !MediaEventFilter.IsRelated(first, filter))
            throw new InvalidDataException("A media segment requires a canonical event with a usable media identity.");
        return filter;
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

    private string RootRelativePath => DirectoryName;
    private string WriterRelativeDirectory => Path.Combine(DirectoryName, "writers", writerId);
    private string RelativeWriterPath(string name) => Path.Combine(WriterRelativeDirectory, name);

    private void EnsureOwnedLayout()
    {
        EnsureOwnedDirectory(RootRelativePath, OwnershipMarkerName, writerId: null);
        fileSystem.EnsureDirectory(Path.Combine(DirectoryName, "writers"));
        EnsureOwnedDirectory(WriterRelativeDirectory, WriterMarkerName, writerId);
    }

    private void ValidateExistingOwnedLayout()
    {
        if (!fileSystem.DirectoryExists(RootRelativePath)) return;
        ValidateExistingOwnedDirectory(RootRelativePath, OwnershipMarkerName, writerId: null);
        var writersRoot = Path.Combine(DirectoryName, "writers");
        if (fileSystem.DirectoryExists(writersRoot)) ValidateWriterDirectories(writersRoot);
        if (fileSystem.DirectoryExists(WriterRelativeDirectory)) ValidateExistingOwnedDirectory(WriterRelativeDirectory, WriterMarkerName, writerId);
    }

    private void EnsureOwnedDirectory(string path, string markerName, string? writerId)
    {
        if (!fileSystem.TryCreateDirectory(path))
        {
            ValidateExistingOwnedDirectory(path, markerName, writerId);
            return;
        }

        var newMarkerPath = Path.Combine(path, markerName);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new MediaOwnershipMarker(OwnershipSchema, writerId), JsonOptions);
        using var output = fileSystem.CreateNew(newMarkerPath);
        output.Write(payload);
        if (output is FileStream fileStream) fileStream.Flush(flushToDisk: true);
        output.Dispose();
        ValidateExistingOwnedDirectory(path, markerName, writerId);
    }

    private void ValidateExistingOwnedDirectory(string path, string markerName, string? writerId)
    {
        var markerPath = Path.Combine(path, markerName);
        if (!fileSystem.FileExists(markerPath)) throw new IOException($"The existing media directory is not marked as Storage Chronicle-owned: {path}");
        using var input = fileSystem.OpenRead(markerPath);
        var existing = JsonSerializer.Deserialize<MediaOwnershipMarker>(input, JsonOptions);
        if (existing is null || existing.Schema != OwnershipSchema || !string.Equals(existing.WriterId, writerId, StringComparison.Ordinal))
        {
            throw new IOException($"The media ownership marker is invalid or belongs to another writer: {markerPath}");
        }

        if (writerId is not null) ValidateWriterEntries(path, markerName);
        else ValidateOwnedRootEntries(path, markerName);
    }

    private void ValidateOwnedRootEntries(string path, string markerName)
    {
        foreach (var entry in fileSystem.EnumerateEntries(path))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"A media root entry is a reparse point: {entry.Name}");
            if (entry.Name.Equals(markerName, StringComparison.OrdinalIgnoreCase) || entry.Name.Equals("recovery-marker.json", StringComparison.OrdinalIgnoreCase))
            {
                if ((entry.Attributes & FileAttributes.Directory) != 0) throw new IOException("The media ownership marker must be a regular file.");
                continue;
            }
            if (entry.Name.Equals("writers", StringComparison.OrdinalIgnoreCase) && (entry.Attributes & FileAttributes.Directory) != 0) continue;
            throw new IOException($"An unrecognized entry exists in the media root and will not be modified: {entry.Name}");
        }
    }

    private void ValidateWriterDirectories(string writersRoot)
    {
        foreach (var entry in fileSystem.EnumerateEntries(writersRoot))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || (entry.Attributes & FileAttributes.Directory) == 0)
                throw new IOException($"An unexpected non-directory or reparse entry exists in the media writers directory: {entry.Name}");
            ValidateComponent(entry.Name, nameof(entry.Name));
        }
    }

    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private void ValidateWriterEntries(string directory, string markerName)
    {
        foreach (var entry in fileSystem.EnumerateEntries(directory))
        {
            var name = entry.Name;
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"A media writer entry is a reparse point: {name}");
            var isSegment = IsGuidFileName(name, ".seg") || IsGuidFileName(name, ".tmp");
            var isManifest = IsManifestGenerationFile(name);
            if (!name.Equals(markerName, StringComparison.OrdinalIgnoreCase) && !isSegment && !isManifest)
            {
                throw new IOException($"An unrecognized entry exists in the media writer directory and will not be modified: {name}");
            }

            if ((entry.Attributes & FileAttributes.Directory) != 0) throw new IOException($"Unexpected directory exists in the media writer directory: {name}");
        }
    }

    private static bool IsGuidFileName(string name, string extension) => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(name.AsSpan(0, name.Length - extension.Length), "N", out _);
    private static bool IsManifestGenerationFile(string name) => TryGetManifestSlot(name, out _);

    private static bool TryGetManifestSlot(string name, out string slot)
    {
        slot = string.Empty;
        foreach (var legacySlot in new[] { "A", "B" })
        {
            if (name.Equals($"manifest-{legacySlot}.json", StringComparison.OrdinalIgnoreCase))
            {
                slot = legacySlot;
                return true;
            }
        }

        var parts = name.Split('-', StringSplitOptions.None);
        if (parts.Length != 3 || !parts[0].Equals("manifest", StringComparison.OrdinalIgnoreCase) || parts[1] is not ("A" or "B")) return false;
        var generation = Path.GetFileNameWithoutExtension(parts[2]);
        var extension = Path.GetExtension(name);
        if (extension is not (".json" or ".tmp") || !Guid.TryParseExact(generation, "N", out _)) return false;
        slot = parts[1];
        return true;
    }
    private sealed record MediaOwnershipMarker(string Schema, string? WriterId);
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
