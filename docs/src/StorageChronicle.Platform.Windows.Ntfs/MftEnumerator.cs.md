# MftEnumerator.cs

`WindowsMftEnumerator` implements lightweight MFT-style enumeration through the documented `FSCTL_ENUM_USN_DATA` public API. It streams file reference number, sequence, parent reference, name, USN, and standard file attributes in bounded batches. It does not parse `$MFT` sectors or read file contents. Access and media errors surface as `NtfsAccessException`; cancellation is honored between batches and records.

`NtfsReconciliationComparer` compares saved File ID, parent, name, and last USN values and returns `NtfsReconciliationCandidate` values rather than ordinary change events. Detailed metadata acquisition can be layered on only for changed directory candidates by the application-selected reconciliation flow.

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
