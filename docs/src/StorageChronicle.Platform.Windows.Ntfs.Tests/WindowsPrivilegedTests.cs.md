# WindowsPrivilegedTests.cs

The opt-in Windows privileged test queries an existing volume journal through the real `WindowsNtfsApi` boundary. It performs no journal creation, deletion, resizing, file-content read, or raw-sector access. The test is enabled with `STORAGE_CHRONICLE_RUN_PRIVILEGED_NTFS=1` and accepts expected access-denied or media-locality conditions.
