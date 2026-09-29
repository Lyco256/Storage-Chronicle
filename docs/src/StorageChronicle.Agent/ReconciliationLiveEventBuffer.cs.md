# ReconciliationLiveEventBuffer.cs

## Role

Provides a bounded, volume-scoped transient bridge for ordinary source events that are durably committed while a confirmed reconciliation scan is running.

## Public types

`ReconciliationLiveEventBuffer` owns one active session per volume. `ReconciliationLiveEventSession` closes the capture window and returns `ReconciliationLiveEventBatch`, which records ordered events and an overflow flag.

## Invariants

Only events after the requested source boundary are captured. Events marked with a reconciliation run ID are excluded. The buffer is bounded and never replaces the durable source log; an overflow causes the reconciliation runner to append a failed-gap fact.

## Dependencies

Depends on the platform-neutral `SourceEvent` and `VolumeId` contracts. `AgentPipeline` calls `Observe` only after source, canonical, and state persistence complete.

## Inputs and outputs

Input is a volume ID, source-sequence boundary, and committed source facts. Output is a bounded ordered batch plus an explicit overflow flag; no file contents or content hashes are accepted.

## Threading and lifetime

The buffer uses a short lock around the session registry and capture list. A session is volume-scoped and must be completed or disposed by the reconciliation caller.

## Failure behavior

Duplicate event IDs are ignored within one session. A full buffer is recorded as overflow rather than silently dropping the reconciliation correctness signal. Sessions are removed on completion or disposal.

## Relevant tests

`tests/StorageChronicle.Agent.Tests/ReconciliationLiveEventBufferTests.cs` covers ordering, source-boundary filtering, reconciliation-fact filtering, bounded overflow, and session cleanup.

## OS constraints

The buffer is platform-neutral; Windows-specific collection remains outside this class.

## Change-sensitive contracts

The volume-scoped session rule, source boundary, bounded capacity, reconciliation-property filter, and overflow flag are integration contracts.
