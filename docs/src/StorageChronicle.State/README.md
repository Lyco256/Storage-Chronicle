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
