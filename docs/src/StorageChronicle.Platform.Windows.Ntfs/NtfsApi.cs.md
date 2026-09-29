# NtfsApi.cs

## Role

`NtfsApi.cs` is the sole native boundary. `WindowsNtfsApi` opens a caller-selected volume handle and calls only `FSCTL_QUERY_USN_JOURNAL`, `FSCTL_READ_USN_JOURNAL`, and `FSCTL_ENUM_USN_DATA`. It maps JournalNotActive, access denied, and media errors into `NtfsApiStatus`; it has no journal-create or journal-resize operation.

## Public contract

`INtfsApi` is injectable for deterministic tests and replaceable by a driver adapter. `UsnJournalData`, `ReadUsnJournalRequest`, and `EnumUsnDataRequest` are managed representations of the documented Windows structures. `NtfsApiLayout` records their wire sizes. `NtfsAccessException` carries a classified status and Win32 error for MFT callers that cannot represent a gap in an item stream. `Windows10CapabilityDetector` reports the Windows 10 22H2-compatible API surface.

## Safety and failure behavior

P/Invoke is limited to `kernel32!CreateFileW` and `DeviceIoControl`. The implementation uses shared volume handles and bounded byte buffers, never `FSCTL_CREATE_USN_JOURNAL`, `FSCTL_DELETE_USN_JOURNAL`, raw `$MFT` sectors, or file contents. Query failures are returned as status; open failures throw `NtfsAccessException` so the agent can isolate the volume.

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
