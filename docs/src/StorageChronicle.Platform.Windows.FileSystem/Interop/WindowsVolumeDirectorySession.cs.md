# WindowsVolumeDirectorySession.cs

`GetOwnedProductDirectoryIdentity()` validates the existing product root through the held directory handle and returns its `FILE_ID_INFO` volume serial and 128-bit file ID. It performs no creation or mutation. Failure to read the ID, validate the volume, or validate the non-reparse owned root is an error; callers must not substitute a path string.

## Role

Implements the Windows volume-bound media filesystem contract. It pins and validates a volume identity and confines production operations to the product-owned `.StorageChronicle` subtree. When a root is first discovered through a handle-relative path, the session duplicates that exact handle after marker/volume validation instead of reopening the name, closing a validation-to-pin rename/swap window.

`InspectProductAcl(approvedUserSid)` opens a read-control volume-root handle, reopens `.StorageChronicle` relative to that handle, checks that it is the same file ID as the session's pinned owner-validated root, and scans existing descendants using no-follow handle-relative opens. It checks the volume-root parent DACL, the product root, and each discovered descendant; reparse entries are inspected as entries but are never followed. The scan is NTFS-only, returns `Unknown` without a fingerprint when traversal is incomplete, and returns a stable descriptor/name/count SHA-256 only after complete traversal. The current-user SID receives no write-like permission at the volume root or product root; beneath `writers`, the scanner allows only one-level writer-folder creation, history-file creation within that writer folder, and append-data for recognized segment/manifest file names. It considers inherit-only ACEs on descendant objects and owner implicit `WRITE_DAC`.

## Public types and responsibilities

`WindowsVolumeDirectorySession` opens the expected volume GUID root, verifies opened objects against that identity, rejects reparse redirection, performs component-wise handle-relative open/create and enumeration, creates files with `FILE_CREATE` and only the data-write/read-attributes/delete/synchronize rights required by the write-and-rename flow, and publishes a session-issued write-only create/recovery stream through handle-based `NtSetInformationFile`. Publication never replaces an existing destination. Existing product roots are opened once with delete sharing disabled and validated through that same pinned handle, avoiding a path re-open between ownership validation and pinning; they must have a valid ownership marker. The internal fixture entry point verifies the mount-point volume identity, then resolves each directory component relative to the pinned volume handle; it never opens the fixture target by its absolute path. Fixture/benchmark entry points are not public production API.

## Inputs and outputs

Production callers use `IVolumeBoundMediaFileSystem` paths relative to the volume root. Reads/recovery return read-only streams; create returns a create-only stream tracked by the session as a publication capability. Publication rejects arbitrary paths and streams. The session does not hash content.

## Dependencies

Uses `SafeFileHandle`, Windows volume identity APIs, `NtCreateFile`, `NtQueryDirectoryFile`, and `NtSetInformationFile`. Windows-specific details are isolated under the Windows filesystem project; the public contract remains platform-neutral.

ACL inspection additionally uses `GetSecurityInfo`, `LocalFree`, `GetVolumeInformationW`, and `RawSecurityDescriptor`; see `WindowsMediaAclInspection.cs.md` for the descriptor policy and evidence limits.

## Invariants

Child opens and enumeration are relative to pinned directory handles. The verified product root remains open without delete sharing for the session lifetime, preventing its rename/removal while the capability is live. Paths are validated component-by-component; rooted paths, `.`/`..`, colon, NUL, and reparse traversal are rejected. Handles whose volume GUID differs from the session identity fail closed. Existing media roots require a valid owner marker; only create-only bootstrap of a root created by this session is allowed. New files are create-only, and handle-based publication has replacement disabled.

## Threading and lifetime

The session owns its volume-root handle and is disposable. Read streams are caller-owned; create/recovery streams are tracked as capabilities and closed when the session is disposed. File/directory creation, recovery registration, directory pinning, and disposal serialize on the session lock so disposal cannot return a newly registered writable stream or create directories after it has completed. Calls are synchronous; the media store/coordinator also serializes mutations.

## Failure behavior

Unsupported OS, invalid volume identity, missing/invalid owner marker, wrong-volume objects, reparse entries, invalid components, failed NTSTATUS, failed no-replace publication, or disposed session fail closed without path-based fallback. ACL inspection additionally returns `Unknown` for a missing product root, unsupported filesystem, inaccessible descriptor, unsupported ACE/mask, or incomplete traversal; it returns `Unsafe` for prohibited grants or null DACLs. A product-owned temporary file may remain after a later failure to preserve recovery evidence; it is never deleted or overwritten by this implementation.

## Tests

`tests/StorageChronicle.Platform.Windows.FileSystem.Tests/VolumeAndMediaTests.cs` covers marker bootstrap/validation, public confinement, handle-bound create and no-replace publication, write-only stream capability, enumeration, traversal rejection, arbitrary-stream refusal, and rejection of a reparse ancestor while opening an existing fixture (where symbolic-link creation is supported), using newly created local temp fixtures. ExternalMedia tests cover store-level ownership, corruption, cancellation, and recovery through an isolated adapter. These tests do not touch external or existing product media.

`WindowsMediaAclPolicyTests.cs` exercises the pure descriptor evaluator using synthetic self-relative ACLs; it does not invoke Windows security APIs or access any user's volume. No live-volume ACL traversal test is included in this change.

## OS constraints

Windows only. This session is wired through `WindowsVolumeDirectorySessionFactory` into external-media collection, mirroring, import, and recovery. Fixture tests/builds do not constitute physical acceptance or certify the separately gated installer and privileged runners.

## Change-sensitive contracts

`FILE_CREATE`, handle-relative operations, owner-marker checks, same-session stream capabilities, volume identity verification, and no-replace publication are safety-sensitive. Do not replace them with path-based APIs, arbitrary stream publication, or replace-existing semantics.
