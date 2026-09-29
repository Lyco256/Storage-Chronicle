# ADR-0002: Append-only log plus rebuildable SQLite

## Status

Accepted.

## Decision

The immutable append log is the durable source of truth. SQLite is a rebuildable index for state, search, and projections and is never the sole history store.

## Rationale

Segment boundaries, CRCs, compression metadata, and replay make corruption and index loss recoverable without deleting history.

## Consequences

Writes must preserve sequence and quality metadata; rebuild paths are first-class and are tested after index removal and segment corruption.

## Verification

`tests/StorageChronicle.Storage.Tests/` and `tests/StorageChronicle.EndToEnd.Tests/`.
