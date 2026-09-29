# ADR-0005: Folder relationships instead of descendant synthetic events

## Status

Accepted.

## Decision

Folder moves and deletes record only the observed parent/name relationship change. Descendant paths are reconstructed from versioned relationships at query time.

## Rationale

This preserves source quality and avoids manufacturing one event per descendant.

## Consequences

State reconstruction must retain tombstones, parent versions, cycle checks, and virtual deleted-folder display.

## Verification

`tests/StorageChronicle.State.Tests/StateEngineTests.cs` and `tests/StorageChronicle.Projection.Tests/`.
