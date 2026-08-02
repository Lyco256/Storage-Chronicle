using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Windows.FileSystem.Interop;

namespace StorageChronicle.Platform.Windows.FileSystem.Volumes;

/// <summary>Enumerates Windows volume GUIDs and all mount points without requiring drive letters.</summary>
public sealed class WindowsVolumeEnumerator : IVolumeEnumerator
{
    private readonly IWindowsVolumeNative native;

    /// <summary>Initializes a volume enumerator.</summary>
    public WindowsVolumeEnumerator(IWindowsVolumeNative? native = null)
    {
        this.native = native ?? new WindowsNativeApi();
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<VolumeDescriptor>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult<IReadOnlyList<VolumeDescriptor>>(Array.Empty<VolumeDescriptor>());
        }

        var descriptors = new List<VolumeDescriptor>();
        foreach (var volume in native.EnumerateVolumes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var volumeId = VolumeId.Create(NormalizeVolumeId(volume.VolumeGuidPath));
            var mountPoints = volume.MountPoints.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var isSystem = mountPoints.Any(IsSystemMountPoint);
            descriptors.Add(new VolumeDescriptor(volumeId, volume.FileSystem, mountPoints, volume.IsReadOnly, volume.DriveType is Interop.DriveType.Removable or Interop.DriveType.CdRom, isSystem, volume.SupportsUsn, volume.IsDirectoryReadable));
        }

        return ValueTask.FromResult<IReadOnlyList<VolumeDescriptor>>(descriptors);
    }

    private static string NormalizeVolumeId(string value) => value.TrimEnd('\0').TrimEnd('\\').ToUpperInvariant();

    private static bool IsSystemMountPoint(string mountPoint)
    {
        var systemDirectory = Path.GetFullPath(Environment.SystemDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var normalized = Path.GetFullPath(mountPoint).TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(systemDirectory, normalized, StringComparison.OrdinalIgnoreCase) ||
               systemDirectory.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Detects Windows collector capabilities and deliberately reports ReFS journal continuity conservatively.</summary>
public sealed class WindowsPlatformCapabilities : IPlatformCapabilities
{
    /// <inheritdoc />
    public bool IsSupported(string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return false;
        }

        return capability switch
        {
            "ReadDirectoryChangesW" => true,
            "ExternalMediaNotifications" => true,
            "ConfigurationManagerDeviceNotifications" => true,
            "NtfsUsnJournal" => true,
            "ReFsJournalContinuity" => false,
            "ReFSJournalContinuity" => false,
            _ => false
        };
    }
}
