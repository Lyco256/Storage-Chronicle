using System.ComponentModel;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Abstractions;
using StorageChronicle.Platform.Windows.FileSystem.Interop;
using StorageChronicle.Platform.Windows.FileSystem.Policy;
using StorageChronicle.Platform.Windows.FileSystem.Volumes;

namespace StorageChronicle.Platform.Windows.FileSystem.Snapshot;

/// <summary>Enumerates accessible metadata in directory batches and never traverses a reparse target.</summary>
public sealed class WindowsVolumeSnapshotReader : IVolumeSnapshotReader
{
    private readonly IWindowsFileMetadataNative native;
    private readonly WindowsExclusionPolicy exclusionPolicy;
    private readonly WindowsFileSystemOptions options;

    /// <summary>Initializes a snapshot reader.</summary>
    public WindowsVolumeSnapshotReader(IWindowsFileMetadataNative native, WindowsExclusionPolicy? exclusionPolicy = null, WindowsFileSystemOptions? options = null)
    {
        this.native = native;
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
            yield return WindowsSourceEventFactory.Gap(volume.Id, "Volume cannot be enumerated as a directory", sequence++);
            yield break;
        }

        var root = WindowsVolumePath.GetRootPath(volume);
        var pendingDirectories = new Stack<(string Path, FileId? ParentFileId)>();
        pendingDirectories.Push((root, null));
        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directoryPath, parentFileId) = pendingDirectories.Pop();
            if (exclusionPolicy.ShouldExclude(directoryPath)) continue;

            var batch = ReadDirectoryBatch(volume, directoryPath, parentFileId, sequence, pendingDirectories, cancellationToken);
            sequence = batch.NextSequence;
            foreach (var sourceEvent in batch.Events)
            {
                yield return sourceEvent;
            }
        }
    }

    private SnapshotDirectoryBatch ReadDirectoryBatch(VolumeDescriptor volume, string directoryPath, FileId? parentFileId, long sequence, Stack<(string Path, FileId? ParentFileId)> pendingDirectories, CancellationToken cancellationToken)
    {
        var entries = new List<SourceEvent>(options.SnapshotBatchSize);
        try
        {
            // Bind traversal to the directory object opened without following a final-component reparse point.
            // The handle remains open while its entries are enumerated, so replacing the path cannot redirect
            // this enumeration to a reparse target.
            using var directoryHandle = native.OpenDirectory(directoryPath);
            var directory = ReadEntry(volume, directoryHandle, directoryPath, parentFileId);
            entries.Add(WindowsSourceEventFactory.Snapshot(volume, directory, sequence++));
            foreach (var child in native.EnumerateDirectory(directoryHandle))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entryPath = Path.Combine(directoryPath, child.Name);
                if (exclusionPolicy.ShouldExclude(entryPath)) continue;
                var entry = ReadEntry(volume, entryPath, directory.FileId, isRoot: false);
                entries.Add(WindowsSourceEventFactory.Snapshot(volume, entry, sequence++));
                if (entry.Kind == FileKind.Directory && !WindowsExclusionPolicy.IsReparsePoint(entry.Attributes))
                {
                    pendingDirectories.Push((entryPath, entry.FileId));
                }

                if (entries.Count == options.SnapshotBatchSize)
                {
                    // The list remains a directory-local batch; subsequent entries are included in the same ordered result.
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            entries.Add(WindowsSourceEventFactory.Gap(volume.Id, $"Access denied while enumerating {directoryPath}", sequence++));
        }
        catch (DirectoryNotFoundException)
        {
            entries.Add(WindowsSourceEventFactory.Gap(volume.Id, $"Directory disappeared while enumerating {directoryPath}", sequence++));
        }
        catch (IOException)
        {
            entries.Add(WindowsSourceEventFactory.Gap(volume.Id, $"I/O failure while enumerating {directoryPath}", sequence++));
        }

        return new SnapshotDirectoryBatch(entries, sequence);
    }

    private NativeSnapshotEntry ReadEntry(VolumeDescriptor volume, string path, FileId? parentFileId, bool isRoot)
    {
        try
        {
            var nativeEntry = native.ReadMetadata(path, isRoot ? null : Directory.GetParent(path)?.FullName);
            return new NativeSnapshotEntry(nativeEntry.FileId, nativeEntry.ParentFileId ?? parentFileId, nativeEntry.Name, nativeEntry.Kind, nativeEntry.LogicalSize, nativeEntry.AllocatedSize, nativeEntry.CreatedUtc, nativeEntry.LastAccessUtc, nativeEntry.LastWriteUtc, nativeEntry.FileSystemChangeUtc, nativeEntry.Attributes, nativeEntry.ReparsePointKind, nativeEntry.IsAccessDenied ? EventQuality.ExistenceOnly : EventQuality.Exact, nativeEntry.Exists);
        }
        catch (UnauthorizedAccessException)
        {
            return MinimalEntry(volume, path, parentFileId, FileKind.Unknown);
        }
        catch (Win32Exception)
        {
            return MinimalEntry(volume, path, parentFileId, FileKind.Unknown);
        }
        catch (IOException)
        {
            return MinimalEntry(volume, path, parentFileId, FileKind.Unknown);
        }
    }

    private NativeSnapshotEntry ReadEntry(VolumeDescriptor volume, Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path, FileId? parentFileId)
    {
        try
        {
            var nativeEntry = native.ReadMetadata(handle, path, Directory.GetParent(path)?.FullName);
            return new NativeSnapshotEntry(nativeEntry.FileId, nativeEntry.ParentFileId ?? parentFileId, nativeEntry.Name, nativeEntry.Kind, nativeEntry.LogicalSize, nativeEntry.AllocatedSize, nativeEntry.CreatedUtc, nativeEntry.LastAccessUtc, nativeEntry.LastWriteUtc, nativeEntry.FileSystemChangeUtc, nativeEntry.Attributes, nativeEntry.ReparsePointKind, nativeEntry.IsAccessDenied ? EventQuality.ExistenceOnly : EventQuality.Exact, nativeEntry.Exists);
        }
        catch (UnauthorizedAccessException)
        {
            return MinimalEntry(volume, path, parentFileId, FileKind.Unknown);
        }
        catch (Win32Exception)
        {
            return MinimalEntry(volume, path, parentFileId, FileKind.Unknown);
        }
        catch (IOException)
        {
            return MinimalEntry(volume, path, parentFileId, FileKind.Unknown);
        }
    }

    private static NativeSnapshotEntry MinimalEntry(VolumeDescriptor volume, string path, FileId? parentFileId, FileKind kind)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = volume.Id.Value;
        return new NativeSnapshotEntry(FileId.Create("path:" + path), parentFileId, name, kind, null, null, null, null, null, null, 0, null, EventQuality.ExistenceOnly, true);
    }
}

internal sealed record SnapshotDirectoryBatch(IReadOnlyList<SourceEvent> Events, long NextSequence);
