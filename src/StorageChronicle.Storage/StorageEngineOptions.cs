using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Storage;

/// <summary>Identifies the part of the append log represented by a record.</summary>
public enum StorageRecordKind : byte
{
    /// <summary>A source fact acquired from a platform source.</summary>
    SourceEvent = 1,
    /// <summary>A canonical event produced by normalization.</summary>
    CanonicalEvent = 2
}

/// <summary>Describes the recording lifecycle exposed by the storage engine.</summary>
public enum RecordingState
{
    /// <summary>The writer accepts records.</summary>
    Running,
    /// <summary>Recording stopped because a durable write could not complete.</summary>
    Stopped,
    /// <summary>Recording stopped because the configured capacity reserve was reached.</summary>
    CapacityStopped,
    /// <summary>Recording was stopped by the owner.</summary>
    Completed
}

/// <summary>Reports the durable boundary reached by the append log.</summary>
public sealed record RecordingStatus(RecordingState State, long LastSequence, long LastSourceSequence, string? Reason);

/// <summary>Provides free-space information without reading any user file contents.</summary>
public interface IStorageCapacityProbe
{
    /// <summary>Returns currently available bytes for the storage directory.</summary>
    long GetAvailableBytes(string storageDirectory);
}

/// <summary>Uses the volume containing the storage directory as the capacity source.</summary>
public sealed class DriveInfoCapacityProbe : IStorageCapacityProbe
{
    /// <inheritdoc />
    public long GetAvailableBytes(string storageDirectory)
    {
        var fullPath = Path.GetFullPath(storageDirectory);
        var root = Path.GetPathRoot(fullPath) ?? throw new IOException("The storage path has no volume root.");
        return new DriveInfo(root).AvailableFreeSpace;
    }
}

/// <summary>Configures immutable segments, SQLite durability, compression, and recovery.</summary>
public sealed record StorageEngineOptions
{
    /// <summary>Creates options for the specified storage directory.</summary>
    public StorageEngineOptions(string storageDirectory)
    {
        StorageDirectory = string.IsNullOrWhiteSpace(storageDirectory)
            ? throw new ArgumentException("A storage directory is required.", nameof(storageDirectory))
            : Path.GetFullPath(storageDirectory);
    }

    /// <summary>Root directory containing segments, manifests, and the rebuildable SQLite database.</summary>
    public string StorageDirectory { get; }

    /// <summary>Maximum approximate uncompressed segment size before rotation.</summary>
    public long SegmentMaxBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Periodic flush interval. The default is five seconds.</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>SQLite busy timeout.</summary>
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Minimum free space reserve. Zero disables the preflight reserve check.</summary>
    public long MinimumFreeBytes { get; init; }

    /// <summary>Enables Zstandard compression when a segment is closed.</summary>
    public bool CompressClosedSegments { get; init; } = true;

    /// <summary>Capacity provider used in addition to handling real write failures.</summary>
    public IStorageCapacityProbe CapacityProbe { get; init; } = new DriveInfoCapacityProbe();

    /// <summary>Stable branch label written to manifests for media/history correlation.</summary>
    public string HistoryBranch { get; init; } = "local";

    internal void Validate()
    {
        if (SegmentMaxBytes < 1024) throw new ArgumentOutOfRangeException(nameof(SegmentMaxBytes));
        if (FlushInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(FlushInterval));
        if (BusyTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(BusyTimeout));
        if (MinimumFreeBytes < 0) throw new ArgumentOutOfRangeException(nameof(MinimumFreeBytes));
        if (CapacityProbe is null) throw new ArgumentNullException(nameof(CapacityProbe));
        if (string.IsNullOrWhiteSpace(HistoryBranch)) throw new ArgumentException("A history branch is required.", nameof(HistoryBranch));
    }
}

/// <summary>Base exception for storage durability and recovery failures.</summary>
public class StorageException : IOException
{
    /// <summary>Creates a storage exception.</summary>
    public StorageException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>Raised when the writer must stop after a capacity-related failure.</summary>
public sealed class StorageCapacityException : StorageException
{
    /// <summary>Creates a capacity exception.</summary>
    public StorageCapacityException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>Raised when a flush or close operation cannot establish a durable boundary.</summary>
public sealed class StorageFlushException : StorageException
{
    /// <summary>Creates a flush exception.</summary>
    public StorageFlushException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>Describes a segment that was skipped during tolerant recovery.</summary>
public sealed record SegmentIssue(string Path, string Reason);

/// <summary>Describes a healthy closed or recoverable append segment.</summary>
public sealed record SegmentInfo(
    Guid SegmentId,
    string Path,
    bool IsCompressed,
    long FirstSequence,
    long LastSequence,
    int RecordCount,
    string ManifestReference,
    string HistoryBranch);
