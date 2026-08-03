# UsnRecordParser.cs

## Role and public types

`UsnRecordParser` parses documented `USN_RECORD_V2` records and the continuation headers returned by `FSCTL_READ_USN_JOURNAL` and `FSCTL_ENUM_USN_DATA`. `UsnReason` centralizes the documented reason flags. `UsnRecord` retains the complete 64-bit file reference, parent reference, USN, reason, name, and standard file attributes; its sequence number prevents stale File ID reuse. `UsnRenamePairer` combines old/new name records only when the complete file reference matches, and drains incomplete old-name observations without silently deleting them.

## Invariants and failure behavior

Record length, version, UTF-16 name offset/length, and continuation headers are validated before decoding. Malformed or unsupported buffers throw `InvalidDataException`; no sector-level `$MFT` input is accepted. The parser allocates only the returned record list and decoded names. Rename pairing is bounded to the current stream and never invents descendant events.

## Dependencies and tests

The parser uses BCL binary/span/text APIs only. `UsnRecordParserTests` covers native layout constants, read/enum continuation values, valid and malformed buffers, and rename pairing with File Reference sequence reuse protection.

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
