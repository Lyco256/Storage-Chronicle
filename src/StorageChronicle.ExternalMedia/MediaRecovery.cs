using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.ExternalMedia;

/// <summary>Detects deleted media log directories and starts a new history branch without deleting prior facts.</summary>
public static class MediaRecovery
{
    /// <summary>Ensures a new layout and durable recovery marker when the media log directory is missing.</summary>
    public static async ValueTask<MediaLogDeletionRecovery> RecoverDeletedLogAsync(string mediaRoot, VolumeId expectedVolumeId, IVolumeBoundMediaFileSystem fileSystem, string logicalMediaId, string writerPcId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalMediaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(writerPcId);
        ValidateComponent(logicalMediaId, nameof(logicalMediaId));
        ValidateComponent(writerPcId, nameof(writerPcId));
        ArgumentNullException.ThrowIfNull(fileSystem);
        var fullRoot = Path.GetFullPath(mediaRoot);
        var logRoot = Path.Combine(fullRoot, ".StorageChronicle");
        var missing = !fileSystem.DirectoryExists(".StorageChronicle");
        using var store = new ExternalMediaStore(fullRoot, writerPcId, expectedVolumeId, fileSystem);
        var source = Encoding.UTF8.GetBytes($"{logicalMediaId}|{writerPcId}|{DateTimeOffset.UtcNow:O}");
        var branch = HistoryBranchId.Create("recovered-" + Convert.ToHexString(SHA256.HashData(source))[..16]);
        var markerRelativePath = Path.Combine(".StorageChronicle", "recovery-marker.json");
        var markerPath = Path.Combine(store.MediaLogDirectory, "recovery-marker.json");
        var marker = new { LogicalMediaId = logicalMediaId, WriterPcId = writerPcId, Branch = branch.Value, PreviousManifestSha256 = (string?)null, CreatedUtc = DateTimeOffset.UtcNow };
        var temporaryName = "recovery-marker-" + Guid.NewGuid().ToString("N") + ".tmp";
        var temporary = Path.Combine(".StorageChronicle", temporaryName);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(marker);
        await using (var output = fileSystem.CreateNew(temporary))
        {
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (output is FileStream fileStream) fileStream.Flush(flushToDisk: true);
            fileSystem.MoveCreatedFile(output, markerRelativePath);
        }
        return new MediaLogDeletionRecovery(missing, branch, null, markerPath);
    }

    private static void ValidateComponent(string value, string parameterName)
    {
        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar)) throw new ArgumentException("A media identity cannot contain path separators.", parameterName);
    }
}
