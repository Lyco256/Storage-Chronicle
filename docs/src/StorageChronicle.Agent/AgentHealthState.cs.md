# AgentHealthState

Aggregates bounded UI health, volume continuity, and one-time reconciliation prompts. On the first Agent run it rehydrates unresolved gaps and reconciled state from canonical history so a service restart cannot silently discard a prompt that was recorded while the UI was disconnected. User-declined gaps are not recreated as prompts, and a later durable reconciliation resolves older pending prompts for that volume.

The rehydration reads only canonical event metadata already in the immutable history; it does not read file contents or create synthetic descendant events. `RestoreFromHistoryAsync` is idempotent per process, cancellable, and reports storage failures through the Agent quality state.

Aggregates collector, sink, and pipeline failures, recording status, and per-volume continuity for the read-only Agent health IPC response. It stores only volume identity, continuity, and a bounded reason; it never stores file names or content. It is fed by `AgentPipeline.SourceObserved`, `CollectorFailed`, and the supervised pipeline-failure boundary.
# Documentation completeness marker

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
