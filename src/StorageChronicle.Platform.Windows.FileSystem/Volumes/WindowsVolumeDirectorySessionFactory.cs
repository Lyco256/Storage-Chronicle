using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Windows.FileSystem.Interop;

namespace StorageChronicle.Platform.Windows.FileSystem.Volumes;

/// <summary>Opens Windows volume sessions after verifying the product-media ownership boundary.</summary>
public sealed class WindowsVolumeDirectorySessionFactory : IVolumeBoundMediaFileSystemFactory
{
    /// <inheritdoc />
    public IVolumeBoundMediaFileSystem Open(VolumeId expectedVolumeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedVolumeId.Value);
        return WindowsVolumeDirectorySession.Open(expectedVolumeId.Value);
    }
}
