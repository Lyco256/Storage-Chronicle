# WindowsVolumePath

Resolves a `VolumeDescriptor` to its direct volume-GUID root whenever the identifier has the Windows volume-GUID form, avoiding traversal through a mounted-folder alias that is itself a reparse point. Synthetic/test identifiers continue to use the first mount point or the identifier fallback. It does not inspect file contents. Tests verify that a GUID volume uses the direct root even when mount points are available; filesystem access and error reporting remain the snapshot/monitor owners' responsibility.
