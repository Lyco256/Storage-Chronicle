# EventNormalizer.cs

## Role

`EventNormalizer` is the platform-neutral boundary between `SourceEvent` and `CanonicalEvent`. It references only Domain contracts and the event-normalizer port; it does not access Windows APIs, storage, UI, paths on disk, file contents, or content hashes.

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
- Only a small structural property allow-list is copied. Keys that could carry content, hashes, or MIME data are excluded.

## Failure behavior and dependencies

Invalid option bounds throw `ArgumentOutOfRangeException`. A reused `EventId` with different source facts throws `NormalizationException`. Cancellation is observed without remembering a canceled event. The implementation has no durable side effects and depends on `StorageChronicle.Domain` and `StorageChronicle.Contracts` only.

## Tests

`tests/StorageChronicle.Normalization.Tests/EventNormalizerTests.cs` covers operation mapping, directory roots, rename/move, recycle/restore, read suppression, file and directory Clipboard copy, same-volume cut, cross-volume rejection, missing metadata, unknown operations, idempotence and corruption, cancellation, forbidden property filtering, and process-quality preservation.
