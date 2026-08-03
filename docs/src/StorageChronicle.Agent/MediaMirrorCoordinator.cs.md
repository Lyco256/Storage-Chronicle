# MediaMirrorCoordinator.cs

This file owns the Agent-side optional external-media mirror writer. It registers `.StorageChronicle` with the same Windows exclusion policy used by live and snapshot collection, batches canonical metadata into immutable CRC32C/SHA-256 segments, and seals A/B manifests linked to the prior manifest and mount session.

The coordinator is driven by canonical events after primary durability. It never receives file contents or hashes, refuses read-only/system/boot/recovery/EFI media, recovers interrupted temporary writes, and isolates mirror I/O from primary recording. Removal and supervised restart flush the bounded batch. Tests cover policy rejection, segment/manifest recovery, batching, and removal sealing.

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
