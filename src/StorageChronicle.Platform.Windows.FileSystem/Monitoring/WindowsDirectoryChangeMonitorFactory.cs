using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Windows.FileSystem.Interop;

namespace StorageChronicle.Platform.Windows.FileSystem.Monitoring;

/// <summary>Creates directory monitors for a volume mount point.</summary>
public interface IWindowsDirectoryChangeMonitorFactory
{
    /// <summary>Creates a monitor rooted at a mount point.</summary>
    WindowsDirectoryChangeMonitor Create(VolumeId volumeId, string rootPath, int bufferSize);

    /// <summary>Creates a monitor by opening the volume root and walking configured root components through pinned directory handles.</summary>
    WindowsDirectoryChangeMonitor Create(VolumeId volumeId, string rootPath, string volumeRootPath, IReadOnlyList<string> rootComponents, int bufferSize);
}

/// <summary>Creates monitors backed by the Windows native ReadDirectoryChangesW boundary.</summary>
public sealed class WindowsDirectoryChangeMonitorFactory : IWindowsDirectoryChangeMonitorFactory
{
    private readonly IWindowsFileMetadataNative fileNative;
    private readonly IWindowsDirectoryChangeNative changeNative;

    /// <summary>Initializes a native monitor factory.</summary>
    public WindowsDirectoryChangeMonitorFactory(IWindowsFileMetadataNative? fileNative = null, IWindowsDirectoryChangeNative? changeNative = null)
    {
        var native = new WindowsNativeApi();
        this.fileNative = fileNative ?? native;
        this.changeNative = changeNative ?? native;
    }

    /// <inheritdoc />
    public WindowsDirectoryChangeMonitor Create(VolumeId volumeId, string rootPath, int bufferSize) => new(volumeId, rootPath, fileNative, changeNative, bufferSize);

    /// <inheritdoc />
    public WindowsDirectoryChangeMonitor Create(VolumeId volumeId, string rootPath, string volumeRootPath, IReadOnlyList<string> rootComponents, int bufferSize)
        => new(volumeId, rootPath, fileNative, changeNative, bufferSize, volumeRootPath: volumeRootPath, rootComponents: rootComponents);
}
