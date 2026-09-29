using System.ComponentModel;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Abstractions;
using StorageChronicle.Platform.Windows.FileSystem.Interop;
using StorageChronicle.Platform.Windows.FileSystem.Policy;

namespace StorageChronicle.Platform.Windows.FileSystem.Snapshot;

/// <summary>Enumerates accessible metadata in directory batches and never traverses a reparse target.</summary>
public sealed class WindowsVolumeSnapshotReader : IVolumeSnapshotReader
{
    private readonly IWindowsFileMetadataNative native;
    private readonly WindowsExclusionPolicy exclusionPolicy;
    private readonly WindowsFileSystemOptions options;

    /// <summary>Initializes a snapshot reader backed by the production Windows metadata boundary.</summary>
    public WindowsVolumeSnapshotReader(WindowsExclusionPolicy? exclusionPolicy = null, WindowsFileSystemOptions? options = null)
        : this(new Interop.WindowsNativeApi(), exclusionPolicy, options) { }

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
            yield return WindowsSourceEventFactory.Gap(volume.Id, "Volume cannot be enumerated as a directory", sequence++, volume.FileSystem);
            yield break;
        }

        var mountRoot = volume.MountPoints.Count == 0 ? volume.Id.Value + Path.DirectorySeparatorChar : volume.MountPoints[0];
        var root = exclusionPolicy.ResolveMonitoringRoot(mountRoot);
        if (root is null) yield break;
        var pendingDirectories = new Stack<(string Path, FileId? ParentFileId)>();
        pendingDirectories.Push((root, null));
        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directoryPath, parentFileId) = pendingDirectories.Pop();
            if (exclusionPolicy.ShouldExclude(directoryPath)) continue;

            await foreach (var batch in ReadDirectoryBatches(volume, directoryPath, parentFileId, sequence, pendingDirectories, cancellationToken).ConfigureAwait(false))
            {
                sequence = batch.NextSequence;
                foreach (var sourceEvent in batch.Events) yield return sourceEvent;
            }
        }
    }

    private async IAsyncEnumerable<SnapshotDirectoryBatch> ReadDirectoryBatches(
        VolumeDescriptor volume,
        string directoryPath,
        FileId? parentFileId,
        long sequence,
        Stack<(string Path, FileId? ParentFileId)> pendingDirectories,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var entries = new List<SourceEvent>(options.SnapshotBatchSize);
        var directoryEnumerator = TryCreateDirectoryEnumerator(directoryPath, out var enumerationError);
        var directory = ReadEntry(volume, directoryPath, parentFileId, isRoot: true);
        entries.Add(WindowsSourceEventFactory.Snapshot(volume, directory, sequence++));
        if (entries.Count == options.SnapshotBatchSize)
        {
            yield return new SnapshotDirectoryBatch(entries, sequence);
            entries = new List<SourceEvent>(options.SnapshotBatchSize);
        }

        if (directoryEnumerator is not null)
        {
            using (directoryEnumerator)
            {
                while (enumerationError is null && TryMoveNext(directoryEnumerator, out var entryPath, out enumerationError))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entryPath is null || exclusionPolicy.ShouldExclude(entryPath)) continue;
                    var entry = ReadEntry(volume, entryPath, directory.FileId, isRoot: false);
                    entries.Add(WindowsSourceEventFactory.Snapshot(volume, entry, sequence++));
                    if (entry.Kind == FileKind.Directory && !WindowsExclusionPolicy.IsReparsePoint(entry.Attributes))
                    {
                        pendingDirectories.Push((entryPath, entry.FileId));
                    }

                    if (entries.Count == options.SnapshotBatchSize)
                    {
                        yield return new SnapshotDirectoryBatch(entries, sequence);
                        entries = new List<SourceEvent>(options.SnapshotBatchSize);
                    }
                }
            }
        }

        if (enumerationError is not null)
        {
            entries.Add(WindowsSourceEventFactory.Gap(volume.Id, enumerationError, sequence++, volume.FileSystem));
        }

        if (entries.Count > 0) yield return new SnapshotDirectoryBatch(entries, sequence);
    }

    private static IEnumerator<string>? TryCreateDirectoryEnumerator(string directoryPath, out string? error)
    {
        try
        {
            error = null;
            return Directory.EnumerateFileSystemEntries(directoryPath, "*", new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, ReturnSpecialDirectories = false, AttributesToSkip = 0 }).GetEnumerator();
        }
        catch (UnauthorizedAccessException)
        {
            error = $"Access denied while enumerating {directoryPath}";
        }
        catch (DirectoryNotFoundException)
        {
            error = $"Directory disappeared while enumerating {directoryPath}";
        }
        catch (IOException)
        {
            error = $"I/O failure while enumerating {directoryPath}";
        }

        return null;
    }

    private static bool TryMoveNext(IEnumerator<string> directoryEnumerator, out string? path, out string? error)
    {
        try
        {
            if (!directoryEnumerator.MoveNext())
            {
                path = null;
                error = null;
                return false;
            }

            path = directoryEnumerator.Current;
            error = null;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            path = null;
            error = "Access denied while enumerating a directory.";
        }
        catch (DirectoryNotFoundException)
        {
            path = null;
            error = "A directory disappeared while enumerating a directory.";
        }
        catch (IOException)
        {
            path = null;
            error = "I/O failure while enumerating a directory.";
        }

        return false;
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

    private static NativeSnapshotEntry MinimalEntry(VolumeDescriptor volume, string path, FileId? parentFileId, FileKind kind)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = volume.Id.Value;
        return new NativeSnapshotEntry(FileId.Create("path:" + path), parentFileId, name, kind, null, null, null, null, null, null, 0, null, EventQuality.ExistenceOnly, true);
    }

}

internal sealed record SnapshotDirectoryBatch(IReadOnlyList<SourceEvent> Events, long NextSequence);
