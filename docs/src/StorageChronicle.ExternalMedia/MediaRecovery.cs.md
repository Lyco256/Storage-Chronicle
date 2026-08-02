# MediaRecovery.cs

Detects a missing `.StorageChronicle` log directory and creates a durable recovery marker plus a new `recovered-*` history branch. The previous history is not deleted or rewritten, and logical-media/writer identifiers are rejected when they contain path traversal. Marker publication uses a temporary file and atomic replacement; cancellation propagates from the write. Tests cover deleted-log recovery and traversal protection.
