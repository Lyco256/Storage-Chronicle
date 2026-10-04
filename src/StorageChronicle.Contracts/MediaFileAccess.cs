using System.IO;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Contracts;

/// <summary>One name and metadata result returned by a volume-bound media directory enumeration.</summary>
public sealed record MediaFileSystemEntry(string Name, FileAttributes Attributes);

/// <summary>Summarizes a read-only, handle-bound ACL inspection of the product-owned media tree.</summary>
public sealed record MediaMirrorAclInspection(
    MediaMirrorAclInspectionStatus Status,
    string? DescriptorFingerprint,
    int InspectedDirectoryCount,
    int InspectedFileCount,
    IReadOnlyList<string> Findings);

/// <summary>Indicates whether the media mirror ACL policy was established.</summary>
public enum MediaMirrorAclInspectionStatus
{
    /// <summary>All inspected ACLs satisfy the caller-supplied product write policy.</summary>
    Verified,
    /// <summary>An ACL grants a prohibited principal or right.</summary>
    Unsafe,
    /// <summary>The filesystem or an ACL could not be inspected completely.</summary>
    Unknown
}

/// <summary>Filesystem access pinned to one verified volume identity.</summary>
/// <remarks>Paths are relative to the volume root; implementations must fail closed on reparse points or identity changes.</remarks>
public interface IVolumeBoundMediaFileSystem : IDisposable
{
    /// <summary>Gets the verified volume identity held by this session.</summary>
    VolumeId VolumeId { get; }

    /// <summary>Gets the file-system identity of the existing, validated <c>.StorageChronicle</c> directory.</summary>
    /// <remarks>The identity is obtained from the pinned directory handle, not from its path. It must not create the directory.</remarks>
    string GetOwnedProductDirectoryIdentity();

    /// <summary>Inspects the volume-root and product-owned media-tree ACLs without changing them.</summary>
    /// <param name="approvedUserSid">The exact authenticated SID allowed the narrow product-history write rights.</param>
    /// <returns>Read-only evidence. Implementations that cannot prove the complete policy must return <see cref="MediaMirrorAclInspectionStatus.Unknown"/>.</returns>
    /// <remarks>
    /// The inspection is anchored to this session's verified volume identity, includes the volume-root parent
    /// and the existing <c>.StorageChronicle</c> tree, and must account for rights such as parent
    /// <c>DELETE_CHILD</c>. It must not read file contents or alter ACLs. A missing product directory may be
    /// reported as verified only when the parent policy and the expected inherited policy for a new directory
    /// are both established.
    /// </remarks>
    MediaMirrorAclInspection InspectProductAcl(string approvedUserSid) => new(
        MediaMirrorAclInspectionStatus.Unknown, null, 0, 0, ["AclInspectionNotImplemented"]);

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
