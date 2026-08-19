using Microsoft.Win32.SafeHandles;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Platform.Windows.FileSystem.Interop;

/// <summary>Native volume information returned without reading file contents.</summary>
public sealed record NativeVolumeRecord(
    string VolumeGuidPath,
    string FileSystem,
    IReadOnlyList<string> MountPoints,
    DriveType DriveType,
    bool IsReadOnly,
    bool IsDirectoryReadable,
    bool SupportsUsn);

/// <summary>Native metadata for one file-system object.</summary>
public sealed record NativeFileMetadataRecord(
    FileId FileId,
    FileId? ParentFileId,
    string Name,
    FileKind Kind,
    long? LogicalSize,
    long? AllocatedSize,
    DateTimeOffset? CreatedUtc,
    DateTimeOffset? LastAccessUtc,
    DateTimeOffset? LastWriteUtc,
    DateTimeOffset? FileSystemChangeUtc,
    FileAttributes Attributes,
    string? ReparsePointKind,
    bool Exists,
    bool IsAccessDenied);

/// <summary>Result of one ReadDirectoryChangesW call.</summary>
public sealed record NativeDirectoryChangeReadResult(byte[] Buffer, int BytesReturned, int ErrorCode);

/// <summary>Thin interface over Win32 volume APIs.</summary>
public interface IWindowsVolumeNative
{
    /// <summary>Enumerates all local volume GUID paths.</summary>
    IReadOnlyList<NativeVolumeRecord> EnumerateVolumes();
}

/// <summary>Thin interface over Win32 file metadata APIs.</summary>
public interface IWindowsFileMetadataNative
{
    /// <summary>Reads identity and standard metadata for an entry.</summary>
    NativeFileMetadataRecord ReadMetadata(string path, string? parentPath = null);

    /// <summary>Opens a metadata-only handle for a file or directory.</summary>
    SafeFileHandle OpenMetadata(string path, bool directory);

    /// <summary>Attempts to open a directory for change monitoring.</summary>
    SafeFileHandle OpenDirectory(string path);
}

/// <summary>Thin interface over ReadDirectoryChangesW.</summary>
public interface IWindowsDirectoryChangeNative
{
    /// <summary>Reads one asynchronous notification buffer from a directory handle.</summary>
    ValueTask<NativeDirectoryChangeReadResult> ReadChangesAsync(SafeFileHandle directoryHandle, int bufferSize, CancellationToken cancellationToken);
}

/// <summary>Thin interface over Windows Configuration Manager device notifications.</summary>
public interface IWindowsDeviceNotificationNative
{
    /// <summary>Registers an event-driven device notification callback.</summary>
    IDisposable Register(Action<ExternalMediaChangeKind> callback);
}
