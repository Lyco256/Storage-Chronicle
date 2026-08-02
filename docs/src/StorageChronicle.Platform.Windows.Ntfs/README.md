# Windows NTFS collector

This Windows-only project owns the narrow NTFS journal and public USN/MFT enumeration boundary. Volume discovery, connection/disconnection, and non-NTFS monitoring remain delegated to `StorageChronicle.Platform.Windows.FileSystem`.

`WindowsNtfsApi` is the only P/Invoke layer. `UsnJournalReader` queries and reads an existing journal without creating or resizing it. `WindowsMftEnumerator` uses `FSCTL_ENUM_USN_DATA`, and `UsnRecordParser` validates documented `USN_RECORD_V2` buffers. No raw `$MFT` sectors, file contents, or content hashes are read.
