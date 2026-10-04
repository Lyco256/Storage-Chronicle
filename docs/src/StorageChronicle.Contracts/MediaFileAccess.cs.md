# MediaFileAccess.cs

`IVolumeBoundMediaFileSystem.GetOwnedProductDirectoryIdentity()` returns the opaque identity of an already-existing, validated `.StorageChronicle` directory using the pinned volume-bound session. The operation is read-only and must not create the directory; absence, invalid ownership evidence, reparse points, or inability to obtain the underlying file identity fails closed. Test-only path adapters deliberately throw because a normalized path is not authoritative file identity evidence.

`InspectProductAcl(approvedUserSid)` provides a read-only inspection result for the verified volume-root parent and existing product-owned media tree. It must account for parent `DELETE_CHILD`, prohibit broad-principal write access and destructive/current-user rights, and return `Unknown` if any handle, ACL, or subtree cannot be inspected. The result carries a descriptor fingerprint and inspected-entry counts; the API does not return file content. Implementations that have not implemented this inspection inherit a fail-closed `Unknown` result.

## Role

Defines the platform-neutral volume-bound filesystem capability used by external-media persistence, so stores do not accept mount-point paths as write authority.

## Public types and responsibilities

`MediaFileSystemEntry` carries one entry name and its attributes. `MediaMirrorAclInspectionStatus` distinguishes verified, unsafe, and unknown ACL outcomes; `MediaMirrorAclInspection` carries the read-only fingerprint, scope counts, and findings. `IVolumeBoundMediaFileSystem` exposes the handle-bound ACL inspection plus directory creation/enumeration, read-only opens, create-only file creation, and non-replacing publication of a file stream issued by that same session. `IVolumeBoundMediaFileSystemFactory` creates a platform implementation for an expected volume identity.

## Inputs and outputs

Paths are relative to a pinned volume root and production implementations confine them to the product-owned `.StorageChronicle` subtree. Implementations reject rooted paths, traversal, reparse redirection, and volume mismatch. Reads return read-only streams. New writes use create-only semantics. Publication accepts only an open create/recovery stream returned by that session and refuses an existing destination; arbitrary source paths or streams are not accepted.

## Dependencies

Depends only on `VolumeId` from the domain contract and standard stream/file-attribute types. Windows handles and APIs remain outside this shared contract.

## Invariants

The session binds one verified `VolumeId` for its lifetime. Callers cannot substitute a mount-point string for the expected identity. Existing `.StorageChronicle` roots require a valid ownership marker and are never adopted when unmarked. The only bootstrap exception is create-only creation of a new product root and ownership marker. ACL inspection is anchored to the same verified session and must include every existing subtree entry without opening file content; if incomplete, its outcome is `Unknown`. Methods do not read file contents unless `OpenRead` is explicitly invoked.

## Threading and lifetime

Implementations are disposable and own pinned OS handles. Callers dispose the session after media I/O and serialize mutations according to the owning store's concurrency policy.

## Failure behavior

Invalid paths, missing entries, unsupported operations, I/O failures, reparse points, cancellation at caller-owned stream operations, unmarked roots, or identity mismatch fail explicitly. Implementations never fall back to unbound path-based I/O.

## Tests

`tests/StorageChronicle.Platform.Windows.FileSystem.Tests/VolumeAndMediaTests.cs` covers handle-bound confinement, create-only writes, publication capability, and no-overwrite behavior. `tests/StorageChronicle.ExternalMedia.Tests` covers store persistence, corruption, cancellation, and recovery through an isolated adapter.

## OS constraints

The contract is platform-neutral and usable by future collectors. Each platform must provide equivalent handle-bound identity guarantees before enabling writes; the Windows implementation is under the Windows filesystem project.

## Change-sensitive contracts

Create-only creation and same-session, no-replace publication are safety-sensitive. Never reintroduce a caller-settable identity boolean, arbitrary path/stream publication, replace-existing option, or absolute-path mutation API.
