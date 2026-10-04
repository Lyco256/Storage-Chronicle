# WindowsPinnedRunOwnedDirectory.cs

Implements handle-pinned access to an existing run-owned NTFS directory for the Windows real-I/O oracle validator. It prevents path substitution between checking the fixture and opening marker/oracle files or creating new validation evidence.

## Role

Provides the narrow Windows-only filesystem boundary used by the validator; it is not used by product collection or persistence.

## Public types and responsibilities

`WindowsPinnedRunOwnedDirectory` is internal. `OpenExisting` validates and pins the existing fixture root. `OpenReadDirectChild` opens a direct child for read-only access. `CreateNewDirectChild` creates a new direct-child output without replacing an existing entry. Callers own returned streams and dispose the session.

## Inputs and outputs

Accepts an existing fixture directory path and single-component child names. Produces read streams, create-new write streams, and the resolved volume GUID. It never reads or stores monitored file contents or file-content hashes.

## Dependencies

Uses Windows `CreateFileW`, `GetVolumePathNameW`, `GetVolumeNameForVolumeMountPointW`, `GetVolumeInformationW`, `GetFinalPathNameByHandleW`, `GetFileInformationByHandleEx`, and handle-relative `NtCreateFile`, isolated in the Windows platform project.

## Invariants

The root must exist on NTFS, resolve to the expected volume-GUID path, and not be a reparse point. Relative operations permit only direct-child names, reject traversal and alternate data streams, reject reparse/directory children, and never overwrite existing data. The root handle denies delete sharing while held. No directory creation, deletion, move, ACL change, or workload-file open is provided.

## Threading and lifetime

The caller keeps the session alive for the entire validation operation and disposes all streams/session deterministically. The pinned handle prevents fixture root rename/delete while active.

## Failure behavior

Unsupported OS, path/volume mismatch, non-NTFS filesystems, reparse points, unsafe child names, missing children, collisions, or native I/O errors fail closed; no alternate path-based fallback is used.

## Tests

`tests/StorageChronicle.RealIoOracleValidator.Tests/OracleValidatorTests.cs` exercises handle-relative create-new, root rename refusal, traversal/ADS rejection, live-volume marker mismatch, and existing-output preservation using local GUID-owned NTFS temporary fixtures. It does not run product/service/installer, physical-media, or privileged acceptance tests.

## OS constraints

Windows-specific implementation, compiled for Windows 10 build 19041 or later. Platform-neutral contracts and collectors do not depend on this helper.

## Change-sensitive contracts

The no-overwrite and root-pinning guarantees are safety-sensitive. Any change to access rights, sharing flags, child-name validation, NT status handling, volume identity verification, or fallback behavior requires focused safety review and tests.
