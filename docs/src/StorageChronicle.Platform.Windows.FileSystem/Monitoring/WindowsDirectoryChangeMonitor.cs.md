# WindowsDirectoryChangeMonitor

Runs the event-driven ReadDirectoryChangesW loop for one mounted root with subtree notifications enabled, so normal monitoring does not perform periodic full scans. Native overflow (`ERROR_NOTIFY_ENUM_DIR`), malformed buffers, access denial, invalid handles, path loss, and device removal produce `ContinuityGap` with `UnverifiedGap` handling upstream. Cancellation is a normal shutdown path and does not create a false gap. The handoff channel is bounded and applies backpressure instead of retaining an unbounded notification queue.

## Role

This mirror documents the source boundary for this file and explains how it participates in Storage Chronicle.

## Public types and responsibilities

Public types preserve source facts and the explicitly owned responsibility; UI interpretation and correlation remain outside this boundary.

## Inputs and outputs

Inputs and outputs are the declared contracts of the source file. File contents and file-content hashes are never an input or output.

## Dependencies

Dependencies are limited to the referenced project contracts and platform services shown by the source file.

## Invariants

The native buffer and managed handoff queue are bounded; a queue write waits under pressure, while native overflow or a lost handle becomes an explicit continuity gap. The source keeps canonical facts distinguishable from reconstructed state and does not synthesize descendant events.

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
