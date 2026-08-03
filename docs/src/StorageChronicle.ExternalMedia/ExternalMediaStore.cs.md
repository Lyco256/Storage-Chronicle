# ExternalMediaStore.cs

Implements the `.StorageChronicle` media mirror with writer-specific immutable segments and writer-specific A/B manifests, CRC32C records, SHA-256 segment identity, temporary-to-atomic publication, mount-session links, and branch detection. Segment and identity file names are validated against traversal. It stores event facts only and never mirrors unrelated PC history.

Public operations append and verify bounded records, publish self-hashed manifests carrying format/schema/projection versions and parent references, select the newest valid A/B slot, and recover at most one temporary segment after an interrupted write. Invalid manifests, segment SHA/CRC mismatches, partial records, ambiguous multiple temporary files, cancellation, and unsupported versions fail or are reported without mutating finalized history. Tests cover round trips, corruption, A/B selection, branch detection, interrupted writes, and mount-session continuity.

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
