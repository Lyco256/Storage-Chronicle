# WindowsVolumeSnapshotReader

Performs depth-first snapshot enumeration in configured bounded event batches. It starts from the direct volume-GUID root (while retaining the selected mount path as the event/exclusion display scope), then reaches a configured subdirectory one component at a time through pinned parent handles. Any reparse point or non-directory in the configured-root chain stops acquisition. Every subsequent child is opened relative to the currently pinned directory handle. Metadata and parent identity are obtained through those handles; recursion carries the same opened non-reparse directory handle. A final reparse point is recorded as an entry but never traversed, including when a directory is swapped to a symlink after enumeration and before open. Access-denied metadata falls back to `ExistenceOnly`; disappeared entries and enumeration/open failures become gap events carrying the volume filesystem. Exclusions are evaluated before metadata acquisition. No file contents or content hashes are read. Tests cover relative-handle ancestry, configured-subdirectory handle traversal, the real Windows temporary-directory swap race, reparse non-recursion, volume-GUID roots, access fallback, cancellation, bounded batches, and no content access.

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
