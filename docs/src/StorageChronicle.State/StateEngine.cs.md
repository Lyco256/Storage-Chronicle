# StateEngine.cs

## Role

Contains the state-engine read models, public state API, typed continuity/corruption exceptions, and the in-memory implementation that applies canonical events.

## Public types

`FileObjectMetadata` and `FileObjectVersion` contain metadata history independent of directory placement. `FileObject` and `DirectoryEntryHistory` expose immutable history snapshots. `AppliedStateEvent` and `StateEventKind` distinguish observation, reconciliation, and gap events. `IStateEngine` is the OS-neutral integration surface, and `StateEngine` implements it.

## Inputs and outputs

`ApplyAsync` consumes a `CanonicalEvent`. It records one state sequence, updates at most the affected object and directory-entry histories, and returns no mutable state. Time and sequence snapshot methods return Domain `FileStateSnapshot` values with immutable arrays, reconstructed paths, virtual-deletion flags, and a separate unplaced collection.

## Important invariants

Source cursors are scoped by volume, mount session, and event origin. A repeated event ID is idempotent; a missing, reversed, or conflicting source sequence throws before mutation. Effective timestamps are monotonic even if the PC clock regresses. Directory moves do not enumerate or write descendant events. Ancestor traversal detects cycles and returns a corruption exception before applying a bad relationship. `parentKnown=true` preserves a null parent as a known volume root, while an explicit unknown-parent marker remains unplaced.

## Failure, cancellation, and lifetime

The implementation is thread-safe for concurrent readers and one or more writers through a private lock. Invalid events, sequence discontinuities, and cycles preserve the pre-call state. Cancellation is checked before locking, after locking, and while building a snapshot. The engine is process-lifetime state and has no I/O or recovery side effects; callers recover by applying the missing event and retrying.

## Tests

`tests/StorageChronicle.State.Tests/StateEngineTests.cs` covers successful transitions, old/new paths, folder descendant reconstruction, delete tombstones, ExistenceOnly and parent unknown, idempotency, sequence gap/reversal, clock regression, reconciliation classification, cycle corruption, cancellation, and recovery. `SyntheticStateFixtureTests.cs` supplies the lazy one-million-node performance fixture.

## OS and integration constraints

The implementation depends only on Domain contracts. It does not implement the shared `IStateStore` adapter because that interface is a shared contract owned by the top agent; adding that adapter must not duplicate or modify the shared contract.
