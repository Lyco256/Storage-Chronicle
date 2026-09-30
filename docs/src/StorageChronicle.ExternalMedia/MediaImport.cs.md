# MediaImport.cs

Implements confirmed-history import from every writer directory on a media root and persists the PC-side deduplication ledger. Imports validate manifests and segments through `ExternalMediaStore`, skip already-known segment hashes, detect multiple writer PCs, report unavailable parents/corrupt segments, and apply the media-only filter before returning events.

The importer does not rewrite media history or infer missing events. The public ledger constructor accepts only a direct `.json` child of the fixed `CommonApplicationData/Storage Chronicle/history/media-ledgers` directory, with a hexadecimal filename stem encoding the logical media ID. The CommonApplicationData root must be absolute, normalized, local, fixed-drive, and free of reparse points. Save preflights existing path components before directory creation and revalidates afterward. A forgeable ownership marker never authorizes arbitrary paths. Only the exact base-ledger name and that ledger's GUID generation names are accepted; all other files, including marker-shaped or otherwise valid-looking JSON, are preserved and block reads/writes. Each save appends a unique immutable generation with create-new and non-replacing atomic publication; legacy base files remain read-only. Corrupt recognized ledgers and interrupted `.tmp` files are preserved and block writes; strict parsing rejects missing or mistyped arrays, unknown/duplicate fields, unsupported schema versions, malformed hashes, and invalid branch identities. Saves cannot drop prior hashes. Cancellation propagates; temps from interrupted ledger saves are retained and block future writes rather than being silently adopted. The internal fixture-path constructor is test-only. Tests cover PC-A to PC-B handoff, deduplication, append-only generation byte preservation, arbitrary-path/forged-marker rejection, unrecognized-JSON preservation, schema corruption, and interrupted-temp preservation.

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
