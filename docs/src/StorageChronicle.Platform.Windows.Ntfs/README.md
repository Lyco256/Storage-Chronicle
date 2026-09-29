# Windows NTFS collector

This Windows-only project owns the narrow NTFS journal and public USN/MFT enumeration boundary. Volume discovery, connection/disconnection, and non-NTFS monitoring remain delegated to `StorageChronicle.Platform.Windows.FileSystem`.

`WindowsNtfsApi` is the only P/Invoke layer. `UsnJournalReader` queries and reads an existing journal without creating or resizing it. `WindowsMftEnumerator` uses `FSCTL_ENUM_USN_DATA`, and `UsnRecordParser` validates documented `USN_RECORD_V2` buffers. No raw `$MFT` sectors, file contents, or content hashes are read.

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
