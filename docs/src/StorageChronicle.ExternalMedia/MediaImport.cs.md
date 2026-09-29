# MediaImport.cs

Implements confirmed-history import from every writer directory on a media root and persists the PC-side deduplication ledger with unique create-new temporary files. Imports validate manifests and segments through `ExternalMediaStore`, skip already-known segment hashes, detect multiple writer PCs, report unavailable parents/corrupt segments, and apply the media-only filter before returning events.

The importer does not rewrite media history or infer missing events. Ledger reads and writes require a marked app-owned non-reparse-point directory, reject unexpected entries, preserve corrupt bytes instead of treating them as empty, and prevent a save from dropping prior hashes. A corrupt or missing segment produces an honest warning and `UnverifiedGap`; a corrupt ledger fails closed so the collector can report a gap without replacing it. Cancellation propagates from directory enumeration and file reads. Tests cover PC-A to PC-B handoff, same-event/segment deduplication, branch inputs, ledger reload, ownership rejection, corruption preservation, and monotonic hashes.

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
