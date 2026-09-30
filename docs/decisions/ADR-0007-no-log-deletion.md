# ADR-0007: History is never deleted

## Status

Accepted.

## Decision

There is no automatic, periodic, uninstall, repair, or individual-event history deletion API. Capacity pressure stops recording and exposes a quality state.

## Rationale

Chronicle integrity is more important than silently losing evidence to satisfy a resource target.

## Consequences

Installer and recovery flows preserve the history root, and performance work must optimize bounded queues and writes without dropping events.

## Verification

`tests/StorageChronicle.Storage.Tests/` and `tests/StorageChronicle.Installer.Tests/`.
