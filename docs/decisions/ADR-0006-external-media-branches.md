# ADR-0006: Immutable external-media segments and branches

## Status

Accepted.

## Decision

External media uses immutable writer segments, checksummed A/B manifests, writer-specific branches, mount sessions, and a persisted import ledger.

## Rationale

Independent PCs and disconnected media can produce valid divergent histories without requiring a shared mutable log.

## Consequences

Imports must deduplicate segments, preserve parent-unavailable and corruption warnings, and never retain file contents or content hashes.

## Verification

`tests/StorageChronicle.ExternalMedia.Tests/`.
