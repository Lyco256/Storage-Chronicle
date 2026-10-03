# WindowsVolumePath

## Role

Provides the Windows filesystem collector's canonical root path for a volume. It prefers the volume GUID path to avoid resolving a mount-point alias when the descriptor contains a Windows volume identifier.

## Public types and responsibilities

`WindowsVolumePath` is an internal helper. `GetRootPath(VolumeDescriptor)` returns the volume GUID root for `\\?\Volume{...}` identifiers; synthetic/test identifiers use the first mount point or the identifier with a trailing directory separator. It does not open handles or read metadata.

## Inputs and outputs

Input is a non-null `VolumeDescriptor`. Output is a root path string. No filesystem contents or content hashes are read.

## Dependencies

Depends on `VolumeDescriptor` and `System.IO.Path`; it does not call Windows native APIs.

## Invariants

The GUID root is preferred whenever its identifier has the recognized volume-GUID prefix. Synthetic identifiers remain usable in tests. Root selection does not change monitoring scope or infer filesystem facts.

## Threading and lifetime

Stateless and synchronous; it owns no handles or other resources.

## Failure behavior

A null descriptor throws `ArgumentNullException`. Path opening and acquisition failures are reported by the snapshot and monitoring components that consume this path.

## Tests

`tests/StorageChronicle.Platform.Windows.FileSystem.Tests/SnapshotTests.cs` verifies direct volume-GUID root selection when a mount point is also present.

## OS constraints

Recognizes Windows volume GUID syntax while leaving path construction based on platform-neutral `Path` APIs. Native Windows behavior remains isolated in the Windows filesystem project.

## Change-sensitive contracts

The GUID-prefix recognition and fallback precedence affect snapshot/monitor roots and must remain aligned with `VolumeDescriptor` and configured scope handling.
