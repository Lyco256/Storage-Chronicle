# UsnRecordParser.cs

Parses documented USN_RECORD_V2 structures with strict bounds validation and no raw `$MFT` sector access. Capability detection is Windows 10 22H2-safe; unsupported APIs are reported instead of invoked.
