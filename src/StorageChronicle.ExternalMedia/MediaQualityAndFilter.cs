using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.ExternalMedia;

/// <summary>Calculates honest media quality and recovery without creating or changing USN journals.</summary>
public static class MediaQuality
{
    /// <summary>Assesses a volume's quality from capabilities and continuity.</summary>
    public static MediaQualityAssessment Assess(MediaVolumeDescriptor volume, bool notificationGap = false)
    {
        ArgumentNullException.ThrowIfNull(volume);
        var fileSystem = volume.FileSystem.Trim().ToUpperInvariant();
        if (volume.IsReadOnly) return new(MediaHistoryQuality.ReadOnly, MediaRecoveryKind.ReadOnly, fileSystem, volume.SupportsUsn, true, "The media is read-only; history can be imported but no mirror write is attempted.");
        if (notificationGap) return new(MediaHistoryQuality.ReconciliationRequired, volume.SupportsUsn ? MediaRecoveryKind.UsnRecovery : MediaRecoveryKind.FullReconciliation, fileSystem, volume.SupportsUsn, false, "Continuity was lost and requires USN recovery or a full reconciliation.");
        if (fileSystem == "NTFS" && volume.SupportsUsn) return new(MediaHistoryQuality.Exact, MediaRecoveryKind.None, fileSystem, true, false, "NTFS USN continuity is available.");
        if (fileSystem is "FAT" or "FAT32" or "EXFAT") return new(MediaHistoryQuality.DirectoryBestEffort, MediaRecoveryKind.FullReconciliation, fileSystem, false, false, "This filesystem has no supported USN recovery; changes are best effort.");
        return new(MediaHistoryQuality.UnsupportedFormat, MediaRecoveryKind.Unsupported, fileSystem, volume.SupportsUsn, false, "The filesystem capability is not recognized.");
    }

    /// <summary>Creates a recovery plan and explicitly never creates or changes a USN journal.</summary>
    public static MediaRecoveryPlan PlanRecovery(MediaVolumeDescriptor volume, bool continuityGap)
    {
        var assessment = Assess(volume, continuityGap);
        return new(assessment.Recovery, assessment.Quality, false, assessment.Explanation);
    }
}

/// <summary>Filters events to the selected external media identity.</summary>
public static class MediaEventFilter
{
    /// <summary>Returns only events belonging to the selected volume, logical media, or mount session.</summary>
    public static IReadOnlyList<CanonicalEvent> Apply(IEnumerable<CanonicalEvent> events, MediaOnlyFilter filter)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(filter);
        return events.Where(value => IsRelated(value, filter)).ToArray();
    }

    /// <summary>Returns true when one event carries the selected media identity.</summary>
    public static bool IsRelated(CanonicalEvent value, MediaOnlyFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (!filter.IsSpecified) return value.VolumeId is not null || value.MountSessionId is not null;
        if (filter.VolumeId is { } volume && value.VolumeId != volume) return false;
        if (filter.MountSessionId is { } mount && value.MountSessionId != mount) return false;
        if (!string.IsNullOrWhiteSpace(filter.LogicalMediaId))
        {
            if (!value.Properties.TryGetValue("media.logicalMediaId", out var logical) || !string.Equals(logical, filter.LogicalMediaId, StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }
}
