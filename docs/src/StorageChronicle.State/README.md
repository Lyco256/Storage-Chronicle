# StorageChronicle.State

## Role

This project reconstructs a period-aware, OS-neutral file-system state from canonical events. It owns file-object history and observed directory-entry relationships while leaving collection, persistence, UI interpretation, and platform APIs to other projects.

## Public contract

`IStateEngine` applies canonical events idempotently, reconstructs snapshots by time or state sequence, and exposes immutable object, relationship, and accepted-event read models. `StateEngine` is the in-memory implementation used by the application adapter and deterministic tests.

## Dependency direction

The project references only `StorageChronicle.Domain`. It intentionally does not reference Windows, SQLite, UI, or the shared application ports; an integration adapter owned by the top agent can connect this implementation to the shared `IStateStore` contract.

## Invariants

- File-object metadata and parent/name directory-entry versions are stored separately.
- A folder move changes one relationship version; descendant paths are resolved through the ancestor chain.
- Same-name entries remain distinct when their `FileId` values differ.
- Source sequence gaps and reverse order fail before state mutation; duplicate event IDs are no-ops.
- Recorded event time is clamped to a monotonic logical effective time when the machine clock moves backwards.
- Snapshots are immutable collections and unknown parents are returned in `UnplacedEntries`.
- Deleted folders retain a tombstone relationship so descendants can be shown under a virtual deleted folder without synthetic descendant events.

## Failure and cancellation

Validation, sequence discontinuity, and relationship cycles throw typed state exceptions before mutation. A failed event leaves the cursor and read model ready for recovery. All public operations honor cancellation before and during snapshot enumeration.

## Tests

`tests/StorageChronicle.State.Tests/StateEngineTests.cs` covers state transitions, point-in-time paths, folder move/delete reconstruction, same-name identities, ExistenceOnly, parent unknown, idempotency, source corruption, clock regression, reconciliation classification, cycle corruption, cancellation, and recovery. `SyntheticStateFixtureTests.cs` validates the lazy one-million-node performance fixture.

## OS constraints

No Windows or file-system APIs are used. File contents and content hashes are never read or stored.

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
