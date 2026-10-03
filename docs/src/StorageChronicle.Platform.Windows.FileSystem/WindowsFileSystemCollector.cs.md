# WindowsFileSystemCollector

Coordinates volume enumeration, initial snapshots, and per-volume event-driven directory monitors. The monitor starts before the snapshot, notifications cross a bounded initial-scan boundary, and overflow/monitor loss becomes an `UnverifiedGap` source event carrying the selected volume filesystem. Both snapshot and monitor roots start from the direct volume GUID and reach configured subdirectories through pinned handle-relative component opens; any reparse/non-directory component blocks traversal before monitoring begins. The display scope remains the configured mount path for exclusion checks and event names. The output, native-read, and live-read channels each use `PipelineChannelCapacity` with wait/backpressure semantics; retained initial-scan notifications have their separate configured limit. Early consumer disposal cancels and awaits the producer and distributor for every volume. Volumes are isolated so access denied, removal, and I/O failures do not stop other volumes; cancellation is scoped to the collector token. Exclusion policy runs before source events are created. NTFS whole-volume scopes are left to the USN adapter, while a configured NTFS subdirectory uses this directory collector to preserve scope.

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
