# ReadOnlyStorageHistoryReader.cs

## Role

Reads authoritative append segments for acceptance and audit consumers without constructing the normal writer engine or touching the rebuildable SQLite index.

## Public types and responsibilities

`ReadOnlyStorageHistoryReader` validates an existing product-owned history directory and returns `ReadOnlyStorageHistorySnapshot`, containing source events, canonical events, and skipped-segment issues. It does not create a missing directory, initialize SQLite, open the normal writer engine, recover an incomplete segment, repair corruption, or write any history file.

## Inputs and outputs

`Read` accepts an existing history directory and optional cancellation token. It returns decoded source/canonical event records and explicit segment issues; corrupt or skipped segments are never silently converted into complete history.

## Invariants

The directory must already exist, have the product ownership marker, contain only recognized owned entries, and have no reparse-point ancestor or child. Events are read from healthy immutable segments. The reader does not open SQLite at all, avoiding WAL shared-memory creation or any index repair. A malformed event payload fails the read; segment framing/checksum/decompression issues are surfaced in the result and callers must not silently treat a partial history as complete.

## Dependencies

Uses `SegmentLog` in read-only enumeration mode, the existing `AppendOnlyStorageEngine` ownership-entry validator, and platform-neutral Domain event contracts. It does not open any SQLite connection or depend on Windows APIs.

## Failure behavior

Missing/unowned roots, path redirection, malformed ownership metadata, invalid event payloads, and I/O failures throw without attempting repair. Corrupt segments are returned as explicit issues and remain unchanged.

## Threading and lifetime

The current scan is synchronous and owns no long-lived file or database handles. Cancellation is checked between decoded records. Callers should run it off UI threads and account for segment-size memory use.

## OS constraints

Platform-neutral. Reparse detection uses filesystem attributes and the same policy as the Storage writer; it has no Windows-only API dependency.

## Relevant tests

`tests/StorageChronicle.Storage.Tests/StorageEngineTests.cs` verifies event reads while preserving every file byte-for-byte, including when the SQLite index is deliberately damaged.

## Change-sensitive contracts

The history ownership marker name/schema and the recognized owned-entry list are safety boundaries. Changes must keep the writer and reader validation aligned and preserve new-only/no-repair behavior.
