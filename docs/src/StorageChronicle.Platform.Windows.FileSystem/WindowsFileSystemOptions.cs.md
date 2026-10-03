# WindowsFileSystemOptions

Defines the maximum snapshot batch, initial-scan notification retention, the `PipelineChannelCapacity` shared by bounded source/native/live handoffs, native notification buffer size, exclusion roots, and filesystem delegation via `SkipFileSystems`. Validation rejects out-of-range capacities and invalid ReadDirectoryChangesW buffers. It has no platform I/O dependency. Tests cover policy, ordering under capacity-one backpressure, and bounded-buffer behavior.

## Role

This mirror documents the source boundary for this file and explains how it participates in Storage Chronicle.

## Public types and responsibilities

Public types preserve source facts and the explicitly owned responsibility; UI interpretation and correlation remain outside this boundary.

## Inputs and outputs

Inputs and outputs are the declared contracts of the source file. File contents and file-content hashes are never an input or output.

## Dependencies

Dependencies are limited to the referenced project contracts and platform services shown by the source file.

## Invariants

External-media notifications include an explicit continuity-gap kind and optional reason. The notification boundary remains metadata-only and does not synthesize descendant file events. The source keeps canonical facts distinguishable from reconstructed state.

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
