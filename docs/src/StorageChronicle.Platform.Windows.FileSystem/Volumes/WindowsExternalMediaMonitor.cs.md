# WindowsExternalMediaMonitor

Wraps the native Configuration Manager notification callback in a bounded asynchronous channel. Connected/disconnected notifications are event-driven and do not poll or enumerate a volume every second. If the queue fills, the monitor emits an explicit continuity-gap notification so the Agent can request reconciliation rather than silently treating the stream as complete. If native registration is unavailable, the monitor remains disposable and exposes the failure so the Agent can record an `UnverifiedGap` without stopping other collectors. Disposal unregisters the callback and completes the channel. Tests use callback, overflow, and failure fakes; privileged OS notification tests belong in the Windows integration category.

## Role

This mirror documents the source boundary for this file and explains how it participates in Storage Chronicle.

## Public types and responsibilities

Public types preserve source facts and the explicitly owned responsibility; UI interpretation and correlation remain outside this boundary.

## Inputs and outputs

Inputs and outputs are the declared contracts of the source file. File contents and file-content hashes are never an input or output.

## Dependencies

Dependencies are limited to the referenced project contracts and platform services shown by the source file.

## Invariants

The callback path is bounded and non-blocking; an overflow is represented as an explicit `ContinuityGap` item. The source keeps canonical facts distinguishable from reconstructed state and does not synthesize descendant events.

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
