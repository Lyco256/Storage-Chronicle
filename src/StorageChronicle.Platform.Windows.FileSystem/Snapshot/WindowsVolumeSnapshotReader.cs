using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Abstractions;
using StorageChronicle.Platform.Windows.FileSystem.Interop;
using StorageChronicle.Platform.Windows.FileSystem.Policy;
using StorageChronicle.Platform.Windows.FileSystem.Volumes;

namespace StorageChronicle.Platform.Windows.FileSystem.Snapshot;

/// <summary>Enumerates accessible metadata in bounded batches through a pinned, handle-relative directory tree.</summary>
public sealed class WindowsVolumeSnapshotReader : IVolumeSnapshotReader
{
    private readonly IWindowsFileMetadataNative native;
    private readonly WindowsExclusionPolicy exclusionPolicy;
    private readonly WindowsFileSystemOptions options;

    /// <summary>Initializes a snapshot reader.</summary>
    public WindowsVolumeSnapshotReader(IWindowsFileMetadataNative native, WindowsExclusionPolicy? exclusionPolicy = null, WindowsFileSystemOptions? options = null)
    {
        this.native = native ?? throw new ArgumentNullException(nameof(native));
        this.exclusionPolicy = exclusionPolicy ?? new WindowsExclusionPolicy(options);
        this.options = (options ?? new WindowsFileSystemOptions()).Validate();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SourceEvent> ReadInitialSnapshotAsync(VolumeDescriptor volume, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);
        var sequence = 0L;
        if (!volume.IsDirectoryReadable)
        {
            yield return WindowsSourceEventFactory.Gap(volume.Id, "Volume cannot be enumerated as a directory", sequence);
            yield break;
        }

        var rootPath = WindowsVolumePath.GetRootPath(volume);
        if (exclusionPolicy.ShouldExclude(rootPath)) yield break;
        var outputBatch = new List<SourceEvent>(options.SnapshotBatchSize);
        var directories = new Stack<DirectoryFrame>();
        SafeFileHandle? rootHandle = null;
        Exception? rootFailure = null;
        NativeSnapshotEntry? rootEntry = null;
        IEnumerator<NativeDirectoryEntry>? rootEntries = null;
        try
        {
            rootHandle = native.OpenDirectory(rootPath);
            rootEntry = ReadEntry(volume, rootHandle, rootPath, null, parentDirectoryHandle: null, FileKind.Directory);
            rootEntries = native.EnumerateDirectory(rootHandle).GetEnumerator();
        }
        catch (Exception exception) when (IsAcquisitionFailure(exception))
        {
            rootFailure = exception;
        }

        if (rootFailure is not null || rootHandle is null || rootEntry is null || rootEntries is null)
        {
            rootHandle?.Dispose();
            yield return WindowsSourceEventFactory.Gap(volume.Id, $"Volume root could not be safely opened or enumerated: {rootFailure?.Message ?? "native root acquisition returned no result"}", sequence);
            yield break;
        }

        outputBatch.Add(WindowsSourceEventFactory.Snapshot(volume, rootEntry, sequence++));
        if (outputBatch.Count == options.SnapshotBatchSize)
        {
            foreach (var sourceEvent in outputBatch) yield return sourceEvent;
            outputBatch.Clear();
        }
        directories.Push(new DirectoryFrame(rootHandle, rootPath, rootEntry.FileId, rootEntries));
        rootHandle = null;
        rootEntries = null;

        try
        {
            while (directories.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = directories.Peek();
                NativeDirectoryEntry? child = null;
                Exception? enumerationFailure = null;
                var hasNext = false;
                try
                {
                    hasNext = current.Entries.MoveNext();
                    if (hasNext) child = current.Entries.Current;
                }
                catch (Exception exception) when (IsAcquisitionFailure(exception))
                {
                    enumerationFailure = exception;
                }

                if (enumerationFailure is not null)
                {
                    directories.Pop().Dispose();
                    outputBatch.Add(WindowsSourceEventFactory.Gap(volume.Id, $"Directory enumeration failed for {current.Path}: {enumerationFailure.Message}", sequence++));
                    if (outputBatch.Count >= options.SnapshotBatchSize)
                    {
                        foreach (var sourceEvent in outputBatch) yield return sourceEvent;
                        outputBatch.Clear();
                    }
                    continue;
                }

                if (!hasNext)
                {
                    directories.Pop().Dispose();
                    continue;
                }

                if (child is null || child.Name is "." or "..") continue;
                var childPath = Path.Combine(current.Path, child.Name);
                if (exclusionPolicy.ShouldExclude(childPath)) continue;

                SafeFileHandle? childHandle = null;
                try
                {
                    Exception? childFailure = null;
                    NativeSnapshotEntry childEntry;
                    try
                    {
                        childHandle = native.OpenChild(current.Handle, child.Name, child.Attributes);
                        childEntry = ReadEntry(volume, childHandle, childPath, current.FileId, current.Handle, GetFallbackKind(child.Attributes));
                    }
                    catch (Exception exception) when (IsAcquisitionFailure(exception))
                    {
                        childFailure = exception;
                        childEntry = MinimalEntry(volume, childPath, current.FileId, GetFallbackKind(child.Attributes));
                    }

                    outputBatch.Add(WindowsSourceEventFactory.Snapshot(volume, childEntry, sequence++));
                    if (outputBatch.Count >= options.SnapshotBatchSize)
                    {
                        foreach (var sourceEvent in outputBatch) yield return sourceEvent;
                        outputBatch.Clear();
                    }

                    if (childFailure is not null && childEntry.Kind == FileKind.Directory && !WindowsExclusionPolicy.IsReparsePoint(childEntry.Attributes))
                    {
                        outputBatch.Add(WindowsSourceEventFactory.Gap(volume.Id, $"Directory could not be safely opened through its parent handle: {childPath}: {childFailure.Message}", sequence++));
                        if (outputBatch.Count >= options.SnapshotBatchSize)
                        {
                            foreach (var sourceEvent in outputBatch) yield return sourceEvent;
                            outputBatch.Clear();
                        }
                    }

                    if (childHandle is not null && childEntry.Kind == FileKind.Directory && !WindowsExclusionPolicy.IsReparsePoint(childEntry.Attributes))
                    {
                        IEnumerator<NativeDirectoryEntry>? childEntries = null;
                        Exception? childEnumerationFailure = null;
                        try { childEntries = native.EnumerateDirectory(childHandle).GetEnumerator(); }
                        catch (Exception exception) when (IsAcquisitionFailure(exception)) { childEnumerationFailure = exception; }
                        if (childEntries is not null)
                        {
                            directories.Push(new DirectoryFrame(childHandle, childPath, childEntry.FileId, childEntries));
                            childHandle = null;
                        }
                        else
                        {
                            outputBatch.Add(WindowsSourceEventFactory.Gap(volume.Id, $"Directory could not be enumerated through its pinned handle: {childPath}: {childEnumerationFailure?.Message ?? "native enumeration returned no result"}", sequence++));
                            if (outputBatch.Count >= options.SnapshotBatchSize)
                            {
                                foreach (var sourceEvent in outputBatch) yield return sourceEvent;
                                outputBatch.Clear();
                            }
                        }
                    }

                    if (outputBatch.Count >= options.SnapshotBatchSize)
                    {
                        foreach (var sourceEvent in outputBatch) yield return sourceEvent;
                        outputBatch.Clear();
                    }
                }
                finally
                {
                    childHandle?.Dispose();
                }
            }
        }
        finally
        {
            rootHandle?.Dispose();
            rootEntries?.Dispose();
            while (directories.Count > 0) directories.Pop().Dispose();
        }

        foreach (var sourceEvent in outputBatch) yield return sourceEvent;
    }

    private NativeSnapshotEntry ReadEntry(VolumeDescriptor volume, SafeFileHandle handle, string path, FileId? parentFileId, SafeFileHandle? parentDirectoryHandle, FileKind fallbackKind)
    {
        try
        {
            var nativeEntry = native.ReadMetadataRelative(handle, path, parentDirectoryHandle);
            return new NativeSnapshotEntry(nativeEntry.FileId, nativeEntry.ParentFileId ?? parentFileId, nativeEntry.Name, nativeEntry.Kind, nativeEntry.LogicalSize, nativeEntry.AllocatedSize, nativeEntry.CreatedUtc, nativeEntry.LastAccessUtc, nativeEntry.LastWriteUtc, nativeEntry.FileSystemChangeUtc, nativeEntry.Attributes, nativeEntry.ReparsePointKind, nativeEntry.IsAccessDenied ? EventQuality.ExistenceOnly : EventQuality.Exact, nativeEntry.Exists);
        }
        catch (UnauthorizedAccessException)
        {
            return MinimalEntry(volume, path, parentFileId, fallbackKind);
        }
        catch (Win32Exception)
        {
            return MinimalEntry(volume, path, parentFileId, fallbackKind);
        }
        catch (IOException)
        {
            return MinimalEntry(volume, path, parentFileId, fallbackKind);
        }
    }

    private static NativeSnapshotEntry MinimalEntry(VolumeDescriptor volume, string path, FileId? parentFileId, FileKind kind)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = volume.Id.Value;
        var attributes = kind switch
        {
            FileKind.Directory => FileAttributes.Directory,
            FileKind.Junction or FileKind.SymbolicLink or FileKind.ReparsePoint => FileAttributes.ReparsePoint,
            _ => FileAttributes.Normal
        };
        return new NativeSnapshotEntry(FileId.Create("path:" + path), parentFileId, name, kind, null, null, null, null, null, null, attributes, null, EventQuality.ExistenceOnly, true);
    }

    private static FileKind GetFallbackKind(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0) return FileKind.ReparsePoint;
        return (attributes & FileAttributes.Directory) != 0 ? FileKind.Directory : FileKind.File;
    }

    private static bool IsAcquisitionFailure(Exception exception) => exception is UnauthorizedAccessException or Win32Exception or IOException or ArgumentException;

    private sealed class DirectoryFrame(SafeFileHandle handle, string path, FileId fileId, IEnumerator<NativeDirectoryEntry> entries) : IDisposable
    {
        public SafeFileHandle Handle { get; } = handle;
        public string Path { get; } = path;
        public FileId FileId { get; } = fileId;
        public IEnumerator<NativeDirectoryEntry> Entries { get; } = entries;

        public void Dispose()
        {
            Entries.Dispose();
            Handle.Dispose();
        }
    }
}
