# WindowsFileSystemOptions

Defines bounded snapshot batches, initial-scan notification capacity, the per-channel collector pipeline capacity, native notification buffer size, and Storage Chronicle/user roots. Validation rejects out-of-range memory bounds and invalid ReadDirectoryChangesW buffer sizes. Full pipeline channels use wait-mode backpressure and do not silently drop notifications; the separate initial-scan retention buffer reports overflow explicitly. It has no platform I/O dependency. Tests cover option bounds, queue overflow signaling, bounded pipeline progress, and cancellation.
