using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.ExternalMedia;

/// <summary>Separate version numbers used by the media interchange format.</summary>
public static class MediaFormatVersions
{
    /// <summary>Current on-disk container format.</summary>
    public const string Format = "1";
    /// <summary>Current EventSchema version represented by a manifest.</summary>
    public static EventSchemaVersion Schema => EventSchemaVersion.Current;
    /// <summary>Current projection interpretation version.</summary>
    public const string Projection = "1";
}

/// <summary>Filesystem capabilities relevant to an external media history.</summary>
public sealed record MediaVolumeDescriptor(
    string LogicalMediaId,
    VolumeId VolumeId,
    string FileSystem,
    bool IsReadOnly,
    bool SupportsUsn,
    bool IsSystemVolume = false,
    bool IsBootVolume = false,
    bool IsRecoveryVolume = false,
    bool IsEfiVolume = false);

/// <summary>Configuration for the optional media mirror.</summary>
public sealed record MediaMirrorConfiguration(bool Enabled, string MediaRoot, bool IsSystemVolume = false, bool IsBootVolume = false, bool IsRecoveryVolume = false, bool IsEfiVolume = false)
{
    /// <summary>Returns whether this configuration can be enabled.</summary>
    public bool IsAllowed => Enabled && !IsSystemVolume && !IsBootVolume && !IsRecoveryVolume && !IsEfiVolume;
}

/// <summary>Quality assigned to media monitoring and recovery facts.</summary>
public enum MediaHistoryQuality
{
    Exact,
    UsnRecovered,
    DirectoryBestEffort,
    ReadOnly,
    ReconciliationRequired,
    UnsupportedFormat,
    UnverifiedGap
}

/// <summary>Reason a media recovery plan was selected.</summary>
public enum MediaRecoveryKind { None, UsnRecovery, FullReconciliation, ReadOnly, Unsupported }

/// <summary>Assessment of a media volume without changing its journal.</summary>
public sealed record MediaQualityAssessment(MediaHistoryQuality Quality, MediaRecoveryKind Recovery, string FileSystem, bool SupportsUsn, bool IsReadOnly, string Explanation);

/// <summary>One media recovery plan produced after disconnect or notification loss.</summary>
public sealed record MediaRecoveryPlan(MediaRecoveryKind Kind, MediaHistoryQuality Quality, bool CreatesUsnJournal, string Explanation);

/// <summary>Outcome of recovering one interrupted temporary segment.</summary>
public sealed record InterruptedSegmentRecovery(string FileName, bool Finalized, bool Discarded, string Reason);

/// <summary>Result of a media log deletion assessment.</summary>
public sealed record MediaLogDeletionRecovery(bool LogWasMissing, HistoryBranchId Branch, string? LostManifestSha256, string MarkerPath);

/// <summary>Warnings emitted while importing media history.</summary>
public enum MediaImportWarning { None, ConcurrentWritersDetected, ParentManifestUnavailable, InvalidManifestSkipped, SegmentCorrupt, ReadOnlyMedia, ReconciliationRequired }

/// <summary>Result of importing confirmed segments from another PC.</summary>
public sealed record MediaImportResult(
    IReadOnlyList<CanonicalEvent> Events,
    IReadOnlyList<string> ImportedManifestHashes,
    int DuplicateSegmentCount,
    HistoryBranchId Branch,
    MediaHistoryQuality Quality,
    IReadOnlyList<MediaImportWarning> Warnings,
    IReadOnlyList<string>? ImportedSegmentHashes = null);

/// <summary>Persistent deduplication state for imported manifest and segment hashes.</summary>
public sealed record MediaImportLedger(
    IReadOnlySet<string> ManifestHashes,
    IReadOnlySet<string> SegmentHashes,
    IReadOnlySet<string> BranchHashes)
{
    /// <summary>Creates an empty ledger.</summary>
    public static MediaImportLedger Empty { get; } = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>Identifies the supported media-only selection semantics.</summary>
public sealed record MediaOnlyFilter(VolumeId? VolumeId = null, string? LogicalMediaId = null, MountSessionId? MountSessionId = null)
{
    /// <summary>Returns true when the filter has a concrete media identity.</summary>
    public bool IsSpecified => VolumeId is not null || !string.IsNullOrWhiteSpace(LogicalMediaId) || MountSessionId is not null;
}

/// <summary>Registers the dedicated media log folder as monitoring-excluded.</summary>
public interface IMediaMonitoringExclusionRegistrar
{
    /// <summary>Registers one path and returns the normalized registered path.</summary>
    string Register(string path);
}

/// <summary>In-memory exclusion registrar used by the agent adapter and tests.</summary>
public sealed class MediaMonitoringExclusionRegistry : IMediaMonitoringExclusionRegistrar
{
    private readonly HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a normalized absolute path.</summary>
    public string Register(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        paths.Add(full);
        return full;
    }

    /// <summary>Returns a snapshot of registered paths.</summary>
    public IReadOnlySet<string> Paths => paths;
}

/// <summary>Clock abstraction for deterministic mount-session tests.</summary>
public interface IMediaClock
{
    /// <summary>Returns the current recorded UTC time.</summary>
    DateTimeOffset UtcNow { get; }
}

internal sealed class SystemMediaClock : IMediaClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
