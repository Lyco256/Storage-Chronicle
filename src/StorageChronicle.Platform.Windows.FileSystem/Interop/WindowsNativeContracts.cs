using Microsoft.Win32.SafeHandles;
using StorageChronicle.Contracts;
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
    bool SupportsUsn,
    ProtectedVolumeRoles ProtectedRoles = ProtectedVolumeRoles.Unknown,
    bool IsProtectedRoleClassificationComplete = false);

/// <summary>Read-only role facts for one Windows storage partition.</summary>
public sealed record NativePartitionRoleRecord(IReadOnlyList<string> AccessPaths, bool? IsSystem, bool? IsBoot, string? GptType, ushort? MbrType);

/// <summary>Result of a fail-closed protected-role classification.</summary>
public sealed record VolumeRoleClassification(ProtectedVolumeRoles Roles, bool IsComplete);

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

/// <summary>One child name and its directory-entry attributes read from an opened directory handle.</summary>
public sealed record NativeDirectoryEntry(string Name, FileAttributes Attributes);

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

    /// <summary>Reads identity and standard metadata for the object already bound to an open handle.</summary>
    NativeFileMetadataRecord ReadMetadata(SafeFileHandle handle, string path, string? parentPath = null);

    /// <summary>Reads metadata for an object and its parent using handles rooted in the same traversal chain.</summary>
    NativeFileMetadataRecord ReadMetadataRelative(SafeFileHandle handle, string path, SafeFileHandle? parentDirectoryHandle);

    /// <summary>Enumerates direct child names from an open, non-reparse directory handle.</summary>
    IEnumerable<NativeDirectoryEntry> EnumerateDirectory(SafeFileHandle directoryHandle);

    /// <summary>Opens a metadata-only handle for a file or directory.</summary>
    SafeFileHandle OpenMetadata(string path, bool directory);

    /// <summary>Attempts to open a directory for change monitoring.</summary>
    SafeFileHandle OpenDirectory(string path);

    /// <summary>Opens one file-system child relative to an already-open directory, without resolving path ancestors or following the final reparse point.</summary>
    SafeFileHandle OpenChild(SafeFileHandle parentDirectoryHandle, string childName, FileAttributes enumeratedAttributes);
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
