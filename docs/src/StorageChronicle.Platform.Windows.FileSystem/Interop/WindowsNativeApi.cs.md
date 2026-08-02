# WindowsNativeApi

Implements the P/Invoke thin layer. It enumerates volume GUID paths and all mount points, reads filesystem/drive/readability/USN capabilities, obtains file identity and standard metadata, opens directory handles, reads ReadDirectoryChangesW buffers, and registers event-driven CM device notifications. It never reads file contents. Win32 errors are preserved for the monitor to classify as buffer loss, handle loss, removal, or access failure. Runtime guards keep non-Windows execution safe.
