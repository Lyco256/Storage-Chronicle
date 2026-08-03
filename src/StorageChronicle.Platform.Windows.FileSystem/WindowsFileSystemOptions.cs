using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Platform.Windows.FileSystem;

/// <summary>Configures bounded Windows file-system acquisition.</summary>
public sealed record WindowsFileSystemOptions
{
    /// <summary>Gets the directory used by Storage Chronicle and excluded from acquisition.</summary>
    public string? StorageChronicleDataRoot { get; init; }

    /// <summary>Gets additional user-selected directory roots to exclude.</summary>
    public IReadOnlyList<string> UserExcludedRoots { get; init; } = Array.Empty<string>();

    /// <summary>Gets monitored directory roots; an empty list means all local readable roots.</summary>
    public IReadOnlyList<string> MonitoredRoots { get; init; } = Array.Empty<string>();

    /// <summary>Gets filesystem names handled by a higher-fidelity collector, such as the NTFS USN collector.</summary>
    public IReadOnlySet<string> SkipFileSystems { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the maximum number of entries emitted in one initial-snapshot batch.</summary>
    public int SnapshotBatchSize { get; init; } = 256;

    /// <summary>Gets the maximum number of live notifications retained during an initial scan.</summary>
    public int InitialNotificationCapacity { get; init; } = 4096;

    /// <summary>Gets the notification buffer size passed to ReadDirectoryChangesW.</summary>
    public int NotificationBufferSize { get; init; } = 64 * 1024;

    /// <summary>Gets a validated copy of the options.</summary>
    internal WindowsFileSystemOptions Validate()
    {
        if (SnapshotBatchSize is < 1 or > 8192)
        {
            throw new ArgumentOutOfRangeException(nameof(SnapshotBatchSize));
        }

        if (InitialNotificationCapacity is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialNotificationCapacity));
        }

        if (NotificationBufferSize is < 4096 or > 1024 * 1024 || NotificationBufferSize % 4096 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(NotificationBufferSize));
        }

        return this;
    }
}

/// <summary>Describes the kind of directory notification reported by Windows.</summary>
public enum DirectoryChangeKind
{
    /// <summary>A file was added.</summary>
    Added,
    /// <summary>A file was removed.</summary>
    Removed,
    /// <summary>A file was changed.</summary>
    Modified,
    /// <summary>A file or directory was renamed from its old name.</summary>
    RenamedOldName,
    /// <summary>A file or directory was renamed to its new name.</summary>
    RenamedNewName
}

/// <summary>Describes one parsed ReadDirectoryChangesW notification.</summary>
public sealed record DirectoryChangeNotification(
    long Sequence,
    DirectoryChangeKind Kind,
    string RelativePath,
    string? OldRelativePath,
    DateTimeOffset ReceivedUtc);

/// <summary>Describes a continuity failure in a volume monitor.</summary>
public sealed record ContinuityGap(
    VolumeId VolumeId,
    DateTimeOffset StartedUtc,
    DateTimeOffset DiscoveredUtc,
    string Reason,
    bool UserDeclined = false);

/// <summary>Describes a parsed notification read and its continuity status.</summary>
public sealed record DirectoryChangeRead(
    IReadOnlyList<DirectoryChangeNotification> Notifications,
    ContinuityGap? Gap,
    bool MonitorLost,
    int NativeErrorCode);

/// <summary>Describes a physical media arrival or removal.</summary>
public enum ExternalMediaChangeKind
{
    /// <summary>A device interface arrived.</summary>
    Connected,
    /// <summary>A device interface was removed.</summary>
    Disconnected,
    /// <summary>A bounded notification queue overflowed and continuity must be reconciled.</summary>
    ContinuityGap
}

/// <summary>Describes an external-media connection change.</summary>
public sealed record ExternalMediaChange(ExternalMediaChangeKind Kind, DateTimeOffset OccurredUtc, string? DevicePath, string? GapReason = null);

/// <summary>Describes the result of a directory reconciliation requested by the user.</summary>
public sealed record DirectoryReconciliationResult(
    IReadOnlyList<DirectoryReconciliationDelta> Changes,
    ContinuityGap? Gap,
    bool Started);

/// <summary>Describes a path-only reconciliation difference without invented process or exact-change time.</summary>
public sealed record DirectoryReconciliationDelta(
    FileId? FileId,
    string? OldRelativePath,
    string? NewRelativePath,
    DirectoryChangeKind Kind,
    EventQuality Quality);
