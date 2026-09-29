using System.IO;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Contracts;

/// <summary>One name and metadata result returned by a volume-bound media directory enumeration.</summary>
public sealed record MediaFileSystemEntry(string Name, FileAttributes Attributes);

/// <summary>Filesystem access pinned to one verified volume identity.</summary>
/// <remarks>Paths are relative to the volume root; implementations must fail closed on reparse points or identity changes.</remarks>
public interface IVolumeBoundMediaFileSystem : IDisposable
{
    /// <summary>Gets the verified volume identity held by this session.</summary>
    VolumeId VolumeId { get; }

    /// <summary>Creates missing directories one component at a time beneath the pinned volume root.</summary>
    void EnsureDirectory(string relativePath);

    /// <summary>Creates exactly one new directory and returns false without modifying it if the name already exists.</summary>
    bool TryCreateDirectory(string relativePath);

    /// <summary>Returns whether a relative path names a non-reparse directory beneath the pinned volume.</summary>
    bool DirectoryExists(string relativePath);

    /// <summary>Returns whether a relative path names an existing non-reparse file beneath the pinned volume.</summary>
    bool FileExists(string relativePath);

    /// <summary>Enumerates names and attributes from a directory resolved beneath the pinned volume.</summary>
    IReadOnlyList<MediaFileSystemEntry> EnumerateEntries(string relativeDirectory);

    /// <summary>Opens an existing non-reparse file for stable, seekable read-only access.</summary>
    Stream OpenRead(string relativePath);

    /// <summary>Opens one GUID-named segment temporary through a pinned handle so validated recovery can finalize that exact file.</summary>
    Stream OpenTemporaryForRecovery(string relativePath);

    /// <summary>Creates a new file without opening or overwriting an existing name.</summary>
    Stream CreateNew(string relativePath);

    /// <summary>Renames a file stream created by this session, or opened for validated recovery, without replacing an existing destination.</summary>
    /// <param name="createdOrRecoveryStream">The exact open stream previously returned by <see cref="CreateNew(string)"/> or <see cref="OpenTemporaryForRecovery(string)"/>.</param>
    /// <param name="relativeDestination">The destination file path relative to the volume root.</param>
    void MoveCreatedFile(Stream createdOrRecoveryStream, string relativeDestination);
}

/// <summary>Opens one handle-bound filesystem session for the supplied volume identity.</summary>
public interface IVolumeBoundMediaFileSystemFactory
{
    /// <summary>Opens and verifies a session for a volume; mismatch or inability to prove identity must throw.</summary>
    IVolumeBoundMediaFileSystem Open(VolumeId expectedVolumeId);
}
