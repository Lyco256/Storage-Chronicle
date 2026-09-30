using StorageChronicle.Contracts;
using StorageChronicle.Platform.Windows.FileSystem.Interop;

namespace StorageChronicle.Platform.Windows.FileSystem.Volumes;

/// <summary>Classifies system-protection roles from one unambiguous, read-only partition record.</summary>
public static class WindowsVolumeRoleClassifier
{
    private static readonly string EfiSystemPartitionType = "C12A7328-F81F-11D2-BA4B-00A0C93EC93B";
    private static readonly string MicrosoftBasicDataPartitionType = "EBD0A0A2-B9E5-4433-87C0-68B6B72699C7";
    private static readonly string MicrosoftRecoveryPartitionType = "DE94BBA4-06D1-4D40-A16A-BFD50179D6AC";
    private static readonly HashSet<ushort> KnownMbrDataPartitionTypes = [0x01, 0x04, 0x06, 0x07, 0x0B, 0x0C, 0x0E];

    /// <summary>Classifies one enumerated volume against a complete storage-partition query.</summary>
    /// <param name="volumeGuidPath">The volume GUID path returned by Windows volume enumeration.</param>
    /// <param name="mountPoints">The current mount paths for that volume.</param>
    /// <param name="partitions">All partition records returned by the same successful query.</param>
    /// <param name="partitionQuerySucceeded">Whether the query completed without error or partial results.</param>
    /// <returns>Known protected roles, or Unknown with an incomplete flag whenever identity or layout is ambiguous.</returns>
    public static VolumeRoleClassification Classify(string volumeGuidPath, IReadOnlyList<string> mountPoints, IReadOnlyList<NativePartitionRoleRecord>? partitions, bool partitionQuerySucceeded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeGuidPath);
        ArgumentNullException.ThrowIfNull(mountPoints);
        if (!partitionQuerySucceeded || partitions is null)
        {
            return Unknown();
        }

        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Normalize(volumeGuidPath) };
        foreach (var mountPoint in mountPoints)
        {
            if (!string.IsNullOrWhiteSpace(mountPoint)) identities.Add(Normalize(mountPoint));
        }

        var matchingPartitions = partitions.Where(partition => partition.AccessPaths.Any(path => !string.IsNullOrWhiteSpace(path) && identities.Contains(Normalize(path)))).ToArray();
        if (matchingPartitions.Length != 1) return Unknown();

        var partition = matchingPartitions[0];
        if (partition.IsSystem is null || partition.IsBoot is null) return Unknown();

        var roles = ProtectedVolumeRoles.None;
        if (partition.IsSystem.Value) roles |= ProtectedVolumeRoles.System;
        if (partition.IsBoot.Value) roles |= ProtectedVolumeRoles.Boot;

        if (!string.IsNullOrWhiteSpace(partition.GptType))
        {
            if (!Guid.TryParse(partition.GptType, out var gptType)) return Unknown(roles);
            if (gptType == Guid.Parse(EfiSystemPartitionType)) roles |= ProtectedVolumeRoles.Efi;
            else if (gptType == Guid.Parse(MicrosoftRecoveryPartitionType)) roles |= ProtectedVolumeRoles.Recovery;
            else if (gptType != Guid.Parse(MicrosoftBasicDataPartitionType)) return Unknown(roles);
        }
        else if (partition.MbrType is ushort mbrType)
        {
            if (mbrType == 0x27) roles |= ProtectedVolumeRoles.Recovery;
            else if (!KnownMbrDataPartitionTypes.Contains(mbrType)) return Unknown(roles);
        }
        else
        {
            return Unknown(roles);
        }

        return new VolumeRoleClassification(roles, true);
    }

    private static string Normalize(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static VolumeRoleClassification Unknown(ProtectedVolumeRoles roles = ProtectedVolumeRoles.None) => new(roles | ProtectedVolumeRoles.Unknown, false);
}
