# WindowsVolumeEnumerator

Maps native volume records to the shared `VolumeDescriptor` contract. Volume GUID identity is retained even with no drive letter, mount points are deduplicated, external media is identified from drive type, and directory-unreadable volumes are reported with `IsDirectoryReadable=false` instead of silently discarded. `WindowsPlatformCapabilities` limits capabilities to Windows 10 19041 APIs and conservatively disables ReFS journal continuity.
