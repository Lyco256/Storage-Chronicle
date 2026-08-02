# ExternalMediaStore.cs

Implements the `.StorageChronicle` media mirror with writer-specific immutable segments, CRC32C records, SHA-256 segment identity, temporary-to-atomic publication, A/B manifests, mount-session links, and branch detection. Segment file names are validated against traversal. It stores event facts only and never mirrors unrelated PC history.
