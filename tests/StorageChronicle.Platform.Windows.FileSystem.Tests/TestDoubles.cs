using Microsoft.Win32.SafeHandles;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Windows.FileSystem;
using StorageChronicle.Platform.Windows.FileSystem.Interop;

namespace StorageChronicle.Platform.Windows.FileSystem.Tests;

internal sealed class FakeVolumeNative(IReadOnlyList<NativeVolumeRecord> volumes) : IWindowsVolumeNative
{
    public IReadOnlyList<NativeVolumeRecord> EnumerateVolumes() => volumes;
}

internal sealed class FakeDeviceNative : IWindowsDeviceNotificationNative
{
    private Action<ExternalMediaChangeKind>? callback;
    public IDisposable Register(Action<ExternalMediaChangeKind> callback)
    {
        this.callback = callback;
        return new DelegateDisposable(() => this.callback = null);
    }

    public void Raise(ExternalMediaChangeKind kind) => callback?.Invoke(kind);
}

internal sealed class FakeFileNative(Func<string, NativeFileMetadataRecord>? metadata = null) : IWindowsFileMetadataNative
{
    private readonly Func<string, NativeFileMetadataRecord> metadata = metadata is null
        ? static path => new NativeFileMetadataRecord(FileId.Create("id:" + path), null, Path.GetFileName(path), FileKind.Directory, null, null, null, null, null, null, FileAttributes.Directory, null, true, false)
        : metadata;
    private readonly Dictionary<nint, string> handlePaths = [];
    private int nextHandle = 1000;
    public List<string> OpenedPaths { get; } = [];
    public List<(nint Parent, string Name)> OpenedChildren { get; } = [];
    public int PathMetadataReadCount { get; private set; }
    public NativeFileMetadataRecord ReadMetadata(string path, string? parentPath = null)
    {
        PathMetadataReadCount++;
        return metadata(path);
    }

    public NativeFileMetadataRecord ReadMetadata(SafeFileHandle handle, string path, string? parentPath = null)
    {
        PathMetadataReadCount++;
        return metadata(path);
    }
    public NativeFileMetadataRecord ReadMetadataRelative(SafeFileHandle handle, string path, SafeFileHandle? parentDirectoryHandle) => metadata(path);
    public IEnumerable<NativeDirectoryEntry> EnumerateDirectory(SafeFileHandle directoryHandle)
    {
        var path = handlePaths[directoryHandle.DangerousGetHandle()];
        return Directory.Exists(path)
            ? Directory.EnumerateFileSystemEntries(path).Select(entry => new NativeDirectoryEntry(Path.GetFileName(entry), File.GetAttributes(entry))).ToArray()
            : Array.Empty<NativeDirectoryEntry>();
    }

    public SafeFileHandle OpenDirectory(string path)
    {
        OpenedPaths.Add(path);
        return CreateHandle(path);
    }

    public SafeFileHandle OpenChild(SafeFileHandle parentDirectoryHandle, string childName, FileAttributes enumeratedAttributes)
    {
        OpenedChildren.Add((parentDirectoryHandle.DangerousGetHandle(), childName));
        return CreateHandle(Path.Combine(handlePaths[parentDirectoryHandle.DangerousGetHandle()], childName));
    }

    private SafeFileHandle CreateHandle(string path)
    {
        var handle = new SafeFileHandle(new IntPtr(Interlocked.Increment(ref nextHandle)), ownsHandle: false);
        handlePaths.Add(handle.DangerousGetHandle(), path);
        return handle;
    }
}

internal sealed class FakeChangeNative(params NativeDirectoryChangeReadResult[] reads) : IWindowsDirectoryChangeNative
{
    private readonly Queue<NativeDirectoryChangeReadResult> pending = new(reads);
    public ValueTask<NativeDirectoryChangeReadResult> ReadChangesAsync(SafeFileHandle directoryHandle, int bufferSize, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(pending.Count == 0 ? new NativeDirectoryChangeReadResult(Array.Empty<byte>(), 0, 1022) : pending.Dequeue());
    }
}

internal sealed class DelegateDisposable(Action dispose) : IDisposable
{
    public void Dispose() => dispose();
}
