# StateEngine.cs

Maintains file-object revisions and parent/name relationship versions. Canonical events are idempotent by EventId, source sequence reuse and reversal are rejected, and snapshots reconstruct paths from the ancestor chain at the requested time. Folder moves never create descendant events; deleted descendants remain queryable as virtual entries. The implementation is lock-bounded and returns immutable arrays.
