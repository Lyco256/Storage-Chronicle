using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.ExternalMedia;

/// <summary>Detects deleted media log directories and starts a new history branch without deleting prior facts.</summary>
public static class MediaRecovery
{
    /// <summary>Ensures a new layout and durable recovery marker when the media log directory is missing.</summary>
    public static async ValueTask<MediaLogDeletionRecovery> RecoverDeletedLogAsync(string mediaRoot, string logicalMediaId, string writerPcId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalMediaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(writerPcId);
        ValidateComponent(logicalMediaId, nameof(logicalMediaId));
        ValidateComponent(writerPcId, nameof(writerPcId));
        var fullRoot = Path.GetFullPath(mediaRoot);
        var logRoot = Path.Combine(fullRoot, ".StorageChronicle");
        var missing = !Directory.Exists(logRoot);
        Directory.CreateDirectory(Path.Combine(logRoot, "writers", writerPcId));
        var source = Encoding.UTF8.GetBytes($"{logicalMediaId}|{writerPcId}|{DateTimeOffset.UtcNow:O}");
        var branch = HistoryBranchId.Create("recovered-" + Convert.ToHexString(SHA256.HashData(source))[..16]);
        var markerPath = Path.Combine(logRoot, "recovery-marker.json");
        var marker = new { LogicalMediaId = logicalMediaId, WriterPcId = writerPcId, Branch = branch.Value, PreviousManifestSha256 = (string?)null, CreatedUtc = DateTimeOffset.UtcNow };
        var temporary = markerPath + ".tmp";
        await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(marker), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, markerPath, true);
        return new MediaLogDeletionRecovery(missing, branch, null, markerPath);
    }

    private static void ValidateComponent(string value, string parameterName)
    {
        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar)) throw new ArgumentException("A media identity cannot contain path separators.", parameterName);
    }
}
