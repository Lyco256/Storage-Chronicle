# WindowsNativeApi

Implements Windows volume/file APIs and the read-only WMI storage-role query. It enumerates volume GUID paths and mount points, queries `MSFT_Partition` AccessPaths/IsSystem/IsBoot/GptType/ MbrType once per enumeration, and passes each result through `WindowsVolumeRoleClassifier`; any WMI/query failure leaves the affected roles unknown. Snapshot descendants are opened with `NtCreateFile` rooted at the already-open parent directory handle, one validated component at a time, and `FILE_OPEN_REPARSE_POINT`; child files request only `FILE_READ_ATTRIBUTES | SYNCHRONIZE`, never file-data access. Metadata, parent identity, and directory enumeration are read from handles. Directory monitoring uses a separately opened overlapped handle and retains the pinned buffer/native state until the exact `ReadDirectoryChangesW` operation completes or is cancelled. The API also reads filesystem/drive/readability/USN capabilities, opens metadata-only handles, and registers event-driven Configuration Manager notifications. It never reads file contents or invokes partition mutation methods. Win32/WMI errors do not become a false unprotected classification. Runtime guards keep non-Windows execution safe; notification registration failure is surfaced as a continuity gap.

## Role

This mirror documents the source boundary for this file and explains how it participates in Storage Chronicle.

## Public types and responsibilities

Public types preserve source facts and the explicitly owned responsibility; UI interpretation and correlation remain outside this boundary.

## Inputs and outputs

Inputs and outputs are the declared contracts of the source file. File contents and file-content hashes are never an input or output.

## Dependencies

Dependencies are limited to the referenced project contracts and platform services shown by the source file.

## Invariants

The source keeps canonical facts distinguishable from reconstructed state and does not synthesize descendant events.

## Threading and lifetime

Callers own cancellation and lifetime; asynchronous work must not outlive the owning pipeline or UI scope.

## Failure behavior

Failure, corruption, cancellation, and recovery remain observable and are not converted into a false successful observation.

## Tests

Validated by tests/StorageChronicle.Integration.Tests and the affected integration tests.

## OS constraints

Platform-neutral behavior remains portable; Windows-only APIs are isolated in the Windows platform projects.

## Change-sensitive contracts

Public names, serialized fields, persistence boundaries, and the mirrored path are compatibility-sensitive contracts.
