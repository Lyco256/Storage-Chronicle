using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Immutable;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.ExternalMedia;

/// <summary>Writes and imports immutable media-related event segments with A/B manifests.</summary>
public sealed class ExternalMediaStore
{
    private const string DirectoryName = ".StorageChronicle";
    private readonly string root;
    private readonly string writerId;

    /// <summary>Initializes a store for one media root and writer PC.</summary>
    public ExternalMediaStore(string mediaRoot, string writerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(writerId);
        root = Path.Combine(Path.GetFullPath(mediaRoot), DirectoryName, "writers", Sanitize(writerId));
        this.writerId = writerId;
        Directory.CreateDirectory(root);
    }

    /// <summary>Writes a segment to a temporary name, validates it, and atomically publishes it.</summary>
    public async ValueTask<MediaSegment> AppendSegmentAsync(IReadOnlyList<CanonicalEvent> events, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        var id = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(root, $"{id}.tmp");
        var final = Path.Combine(root, $"{id}.seg");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                foreach (var value in events)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var payload = JsonSerializer.SerializeToUtf8Bytes(value);
                    await stream.WriteAsync(BitConverter.GetBytes(payload.Length), cancellationToken).ConfigureAwait(false);
                    await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                    var crc = MediaCrc32C.Compute(payload);
                    await stream.WriteAsync(crc, cancellationToken).ConfigureAwait(false);
                }
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, final);
            var sha = await ComputeSha256Async(final, cancellationToken).ConfigureAwait(false);
            return new MediaSegment(Path.GetFileName(final), sha, events.Count, writerId);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    /// <summary>Publishes a manifest through an A/B slot and returns its content hash.</summary>
    public async ValueTask<MediaManifest> PublishManifestAsync(string logicalMediaId, string? parentManifestSha256, string mountSessionId, IReadOnlyList<MediaSegment> segments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalMediaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(mountSessionId);
        var manifest = new MediaManifest("1", EventSchemaVersion.Current, logicalMediaId, writerId, mountSessionId, parentManifestSha256, segments, DateTimeOffset.UtcNow);
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest);
        var sha = Convert.ToHexString(SHA256.HashData(payload));
        var index = await ReadManifestSlotAsync(cancellationToken).ConfigureAwait(false) is null ? "A" : "B";
        var temporary = Path.Combine(root, $"manifest-{index}.tmp");
        var final = Path.Combine(root, $"manifest-{index}.json");
        await File.WriteAllBytesAsync(temporary, payload, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, final, true);
        return manifest with { Sha256 = sha };
    }

    /// <summary>Reads the newest valid A/B manifest and validates its segment names.</summary>
    public async ValueTask<MediaManifest?> ReadManifestSlotAsync(CancellationToken cancellationToken = default)
    {
        foreach (var path in new[] { Path.Combine(root, "manifest-A.json"), Path.Combine(root, "manifest-B.json") })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var manifest = JsonSerializer.Deserialize<MediaManifest>(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
                if (manifest is null || manifest.FormatVersion != "1") continue;
                foreach (var segment in manifest.Segments) EnsureInsideRoot(segment.FileName);
                return manifest;
            }
            catch (JsonException) { }
            catch (InvalidDataException) { }
        }
        return null;
    }

    /// <summary>Returns an explicit branch when two valid manifests share a parent.</summary>
    public static HistoryBranchId DetectBranch(IEnumerable<MediaManifest> manifests)
    {
        var duplicateParent = manifests.GroupBy(value => value.ParentManifestSha256, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Key is not null && group.Count() > 1);
        return duplicateParent is null ? HistoryBranchId.Create("linear") : HistoryBranchId.Create($"branch-{Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(duplicateParent.Key!)))[..16]}");
    }

    private void EnsureInsideRoot(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName)) throw new InvalidDataException("Media segment path traversal is not allowed.");
        var candidate = Path.GetFullPath(Path.Combine(root, fileName));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Media segment is outside the media log directory.");
    }

    private static string Sanitize(string value) => string.Concat(value.Where(value => char.IsLetterOrDigit(value) || value is '-' or '_'));
    private static async ValueTask<string> ComputeSha256Async(string path, CancellationToken cancellationToken) { await using var stream = File.OpenRead(path); var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false); return Convert.ToHexString(hash); }
}

/// <summary>Describes one immutable media segment.</summary>
public sealed record MediaSegment(string FileName, string Sha256, int RecordCount, string WriterPcId);

/// <summary>Describes a complete manifest and its immutable parent reference.</summary>
public sealed record MediaManifest(string FormatVersion, EventSchemaVersion SchemaVersion, string LogicalMediaId, string WriterPcId, string MountSessionId, string? ParentManifestSha256, IReadOnlyList<MediaSegment> Segments, DateTimeOffset CreatedUtc, string? Sha256 = null);

/// <summary>Tracks media mount sessions without correcting clock values.</summary>
public sealed class MountSessionTracker
{
    private MountSession? previous;
    /// <summary>Starts a session linked to the previous session.</summary>
    public MountSession Start(VolumeId volumeId, string pcId, MonitoringContinuity continuity) { var session = new MountSession(MountSessionId.Create(Guid.NewGuid().ToString("N")), volumeId, pcId, DateTimeOffset.UtcNow, null, previous?.Id, continuity, ImmutableArray<string>.Empty); previous = session; return session; }
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
        crc = ~crc;
        return BitConverter.GetBytes(crc);
    }
}
