# WindowsVolumeDirectorySession.cs

## Role

Implements the Windows volume-bound media filesystem contract. It pins and validates a volume identity and confines production operations to the product-owned `.StorageChronicle` subtree.

## Public types and responsibilities

`WindowsVolumeDirectorySession` opens the expected volume GUID root, verifies opened objects against that identity, rejects reparse redirection, performs component-wise handle-relative open/create and enumeration, creates files with `FILE_CREATE`, and publishes a session-issued create/recovery stream through handle-based `NtSetInformationFile`. Publication never replaces an existing destination. Existing product roots must have a valid ownership marker. Internal fixture/benchmark entry points are not public production API.

## Inputs and outputs

Production callers use `IVolumeBoundMediaFileSystem` paths relative to the volume root. Reads/recovery return read-only streams; create returns a create-only stream tracked by the session as a publication capability. Publication rejects arbitrary paths and streams. The session does not hash content.

## Dependencies

Uses `SafeFileHandle`, Windows volume identity APIs, `NtCreateFile`, `NtQueryDirectoryFile`, and `NtSetInformationFile`. Windows-specific details are isolated under the Windows filesystem project; the public contract remains platform-neutral.

## Invariants

Child opens and enumeration are relative to pinned directory handles. The verified product root remains open without delete sharing for the session lifetime, preventing its rename/removal while the capability is live. Paths are validated component-by-component; rooted paths, `.`/`..`, colon, NUL, and reparse traversal are rejected. Handles whose volume GUID differs from the session identity fail closed. Existing media roots require a valid owner marker; only create-only bootstrap of a root created by this session is allowed. New files are create-only, and handle-based publication has replacement disabled.

## Threading and lifetime

The session owns its volume-root handle and is disposable. Read streams are caller-owned; create/recovery streams are tracked as capabilities and closed when the session is disposed. File/directory creation, recovery registration, directory pinning, and disposal serialize on the session lock so disposal cannot return a newly registered writable stream or create directories after it has completed. Calls are synchronous; the media store/coordinator also serializes mutations.

## Failure behavior

Unsupported OS, invalid volume identity, missing/invalid owner marker, wrong-volume objects, reparse entries, invalid components, failed NTSTATUS, failed no-replace publication, or disposed session fail closed without path-based fallback. A product-owned temporary file may remain after a later failure to preserve recovery evidence; it is never deleted or overwritten by this implementation.

## Tests

`tests/StorageChronicle.Platform.Windows.FileSystem.Tests/VolumeAndMediaTests.cs` covers marker bootstrap/validation, public confinement, handle-bound create and no-replace publication, enumeration, traversal rejection, and arbitrary-stream refusal using a newly created local temp fixture. ExternalMedia tests cover store-level ownership, corruption, cancellation, and recovery through an isolated adapter. These tests do not touch external or existing product media.

## OS constraints

Windows only. This session is wired through `WindowsVolumeDirectorySessionFactory` into external-media collection, mirroring, import, and recovery. Fixture tests/builds do not constitute physical acceptance or certify the separately gated installer and privileged runners.

## Change-sensitive contracts

`FILE_CREATE`, handle-relative operations, owner-marker checks, same-session stream capabilities, volume identity verification, and no-replace publication are safety-sensitive. Do not replace them with path-based APIs, arbitrary stream publication, or replace-existing semantics.
