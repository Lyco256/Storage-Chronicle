# WindowsNativeContracts

Contains injectable, thin native boundaries for volume enumeration, metadata/file handles, ReadDirectoryChangesW, and Configuration Manager device notifications. The records contain standard metadata and identity only; no file contents or hashes are represented. Fakes in the Windows filesystem tests exercise the higher layers without privileged Win32 state.
