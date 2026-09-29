# MediaRecovery.cs

Detects an absent `.StorageChronicle` log directory and creates a durable, uniquely named recovery marker plus a new `recovered-*` history branch. If the path already exists (including as a file), recovery fails closed and leaves all content untouched. The previous history is never deleted or rewritten, and logical-media/writer identifiers are rejected when they contain path traversal. New marker publication uses `CreateNew`; cancellation propagates from the write. Tests cover deleted-log recovery and preservation of existing contents.
