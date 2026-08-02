# State Engine handoff

## Assigned requirement

`Requirements/10_AGENT_STATE_ENGINE.md` on branch `feat/state-engine`.

## Owned paths

- `src/StorageChronicle.State/**`
- `tests/StorageChronicle.State.Tests/**`
- `docs/src/StorageChronicle.State/**`
- `docs/handoffs/state-engine.md`

No `Requirements/`, shared contract, solution, Directory, or other project files were changed.

## Changes

- Added the OS-neutral `StorageChronicle.State` project referencing Domain only.
- Implemented `StateEngine` and `IStateEngine` with separated `FileObject` metadata versions and `DirectoryEntryVersion` relationship history.
- Added time and state-sequence snapshots, ancestor-chain path reconstruction, unplaced-parent handling, ExistenceOnly quality preservation, deleted-folder tombstones, virtual descendant display, cycle detection, same-name distinct IDs, source cursor continuity checks, idempotent event application, reconciliation classification, and monotonic logical time for clock regression.
- Added typed validation, gap, order, and corruption exceptions. Failed applications are transactional with respect to the in-memory indexes.
- Added a lazy one-million-node synthetic performance fixture.
- Added source-mirror documentation for the project and implementation.

## Design decisions

Source sequence cursors are scoped by volume, mount session, and origin because `SourceSequence` is source-local. The first accepted value for each cursor must be 1. A duplicate event ID is a no-op; a different event reusing or skipping a sequence is rejected before state mutation. A machine-clock regression is recorded through applied-event effective time by clamping the logical time, preserving source order without rewriting the source timestamp.

Folder moves and deletes update only the folder's relationship history. Descendant relationships remain unchanged and are resolved through the parent chain. A deleted directory retains its last relationship as a tombstone, allowing active descendants to be rendered below a virtual deleted folder while remaining distinguishable from ordinary active state.

## Tests and results

Command:

```text
dotnet test tests/StorageChronicle.State.Tests/StorageChronicle.State.Tests.csproj
```

Result: 12 passed, 0 failed, 0 skipped, 0 warnings/errors during build (including the synthetic fixture and known-root move tests).

The implementation project was also built directly with:

```text
dotnet build src/StorageChronicle.State/StorageChronicle.State.csproj
```

Result: 0 warnings, 0 errors.

## Performance

The fixture represents 1,000,000 deterministic node numbers lazily, so a benchmark harness can stream the workload without retaining a full event list. No descendant events are generated for folder moves or deletes, and per-file histories are indexed by `FileId`; full snapshot materialization remains explicit at the read boundary.

## Known limitations

- The Domain `DirectoryEntryVersion` contract identifies a relationship by `FileId` and does not carry a distinct hard-link entry ID. The engine therefore retains the observed relationship history but does not claim to enumerate a complete hard-link set.
- The state project intentionally does not modify `StorageChronicle.slnx` or implement the shared `IStateStore` adapter. The top agent must add the project/test entries to the solution and connect `StateEngine` to the shared port without changing the shared contract.
- Persistence, SQLite rebuild, and cross-process recovery are owned by the storage/integration agents. This implementation preserves state after a rejected event and supports recovery by retrying after the missing event is supplied.

## Top-agent review points

- Verify the shared solution references are added by the top agent, not this feature branch.
- Apply the integration adapter to the shared `IStateStore` contract without duplicating Domain or Contracts types.
- Run the repository fast/full test and documentation mirror gates after integrating the branch.
- Confirm the top-level 100万ノード benchmark uses the lazy fixture and measures path reconstruction separately from correctness tests.

## Shared-contract change request

No shared-contract change is requested. The only integration request is solution registration and the application-owned adapter, both outside this branch's ownership paths.
