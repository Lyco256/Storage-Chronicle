# UsnRecordParser.cs

## Role and public types

`UsnRecordParser` parses documented `USN_RECORD_V2` records and the continuation headers returned by `FSCTL_READ_USN_JOURNAL` and `FSCTL_ENUM_USN_DATA`. `UsnReason` centralizes the documented reason flags. `UsnRecord` retains the complete 64-bit file reference, parent reference, USN, reason, name, and standard file attributes; its sequence number prevents stale File ID reuse. `UsnRenamePairer` combines old/new name records only when the complete file reference matches, and drains incomplete old-name observations without silently deleting them.

## Invariants and failure behavior

Record length, version, UTF-16 name offset/length, and continuation headers are validated before decoding. Malformed or unsupported buffers throw `InvalidDataException`; no sector-level `$MFT` input is accepted. The parser allocates only the returned record list and decoded names. Rename pairing is bounded to the current stream and never invents descendant events.

## Dependencies and tests

The parser uses BCL binary/span/text APIs only. `UsnRecordParserTests` covers native layout constants, read/enum continuation values, valid and malformed buffers, and rename pairing with File Reference sequence reuse protection.
