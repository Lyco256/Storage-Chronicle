# WindowsFileSystemOptions

Defines bounded snapshot batches, initial-scan notification capacity, native notification buffer size, exclusion roots, and filesystem delegation via `SkipFileSystems`. Validation prevents unbounded memory or invalid ReadDirectoryChangesW buffers. It has no platform I/O dependency. Tests cover policy and bounded-buffer behavior.
