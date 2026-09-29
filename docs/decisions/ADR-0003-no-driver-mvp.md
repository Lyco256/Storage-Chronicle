# ADR-0003: No kernel driver in MVP

## Status

Accepted.

## Decision

The MVP uses public Windows APIs and keeps collector contracts replaceable so a future driver can be added without changing canonical event semantics.

## Rationale

This limits installation and recovery risk while retaining explicit seams for higher-fidelity collection later.

## Consequences

Continuity and quality must be reported honestly when public APIs cannot observe an operation; no fake precision is permitted.

## Verification

`tests/StorageChronicle.Architecture.Tests/` and `tests/StorageChronicle.Platform.Windows.Integration.Tests/`.
