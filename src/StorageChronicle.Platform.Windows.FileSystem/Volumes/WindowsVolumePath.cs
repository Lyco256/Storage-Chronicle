using StorageChronicle.Contracts;

namespace StorageChronicle.Platform.Windows.FileSystem.Volumes;

/// <summary>Resolves a volume's direct GUID root instead of traversing a mount-point alias.</summary>
internal static class WindowsVolumePath
{
    public static string GetRootPath(VolumeDescriptor volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        var volumeId = volume.Id.Value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (volumeId.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase))
        {
            return volumeId + Path.DirectorySeparatorChar;
        }

        return volume.MountPoints.Count == 0 ? volumeId + Path.DirectorySeparatorChar : volume.MountPoints[0];
    }
}
