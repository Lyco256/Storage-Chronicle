# EventNormalizer.cs

## Role

This file is the single source-to-canonical operation and quality mapping for Storage Chronicle. It preserves explicit continuity failures before applying the reconciliation-origin mapping, so a failed or interrupted scan remains an `UnverifiedGap` instead of being displayed as a discovered reconciliation fact.

## Role

`EventNormalizer` is the platform-neutral boundary between `SourceEvent` and `CanonicalEvent`. It preserves the declared filesystem property used by reconciliation prompts, references only Domain contracts and the event-normalizer port, and does not access Windows APIs, storage, UI, paths on disk, file contents, or content hashes.

## Public types

- `NormalizationOptions` bounds the in-memory source-event replay cache and Clipboard candidate buffer and controls the short Clipboard validity interval.
- `NormalizationException` reports contradictory facts for a previously seen `EventId`; the normalizer never silently chooses between two source records.
- `EventNormalizer` implements `IEventNormalizer`. `Normalize` is deterministic for one source event, and `NormalizeAsync` adds cancellation before and after the conversion.

## Invariants

- The source `EventId` is retained as the canonical `EventId`; repeating the same immutable source event returns the same canonical value.
- `Read`, `Open`, `Query`, and `DirectoryEnumeration` ETW observations return `null`, so they cannot enter durable canonical history. Clipboard observations are also retained only in a bounded transient buffer.
- Rename/Move keeps the typed volume, file, parent, old-name, new-name, and source ordering facts. A folder move produces only the observed root event; descendants are reconstructed elsewhere.
- Recycle and Restore are distinct operations. A cut becomes `Move` only after the Clipboard generation, same volume, same File ID, and changed parent are all confirmed. Cross-volume or incomplete cut evidence remains a normal create with a rejection marker.
- ETW process attribution is copied as supplied and is never upgraded from `Correlated` to `Exact`. Missing USN metadata is retained with `JournalOnly` quality; reconciliation is marked `Reconciled`; unknown operation is retained as `UnverifiedGap`.
- Only a small structural property allow-list is copied. Reconciliation run IDs, uncertainty boundaries, metadata quality, scan counts, and user decisions are retained so a restart can explain the durable reconciliation outcome; keys that could carry content, hashes, or MIME data are excluded.

## Failure behavior and dependencies

Invalid option bounds throw `ArgumentOutOfRangeException`. A reused `EventId` with different source facts throws `NormalizationException`. Cancellation is observed without remembering a canceled event. The implementation has no durable side effects and depends on `StorageChronicle.Domain` and `StorageChronicle.Contracts` only.

## Tests

`tests/StorageChronicle.Normalization.Tests/EventNormalizerTests.cs` covers operation mapping, directory roots, rename/move, recycle/restore, read suppression, file and directory Clipboard copy, same-volume cut, cross-volume rejection, missing metadata, unknown operations, idempotence and corruption, cancellation, forbidden property filtering, and process-quality preservation.

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
