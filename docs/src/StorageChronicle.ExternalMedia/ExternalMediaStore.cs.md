# ExternalMediaStore.cs

Implements the `.StorageChronicle` media mirror with writer-specific immutable segments and writer-specific A/B manifests, CRC32C records, SHA-256 segment identity, temporary-to-atomic publication, mount-session links, and branch detection. Segment and identity file names are validated against traversal. It stores event facts only and never mirrors unrelated PC history.

Public operations append and verify bounded records, publish self-hashed manifests carrying format/schema/projection versions and parent references, select the newest valid A/B slot, and recover at most one temporary segment after an interrupted write. Invalid manifests, segment SHA/CRC mismatches, partial records, ambiguous multiple temporary files, cancellation, and unsupported versions fail or are reported without mutating finalized history. Tests cover round trips, corruption, A/B selection, branch detection, interrupted writes, and mount-session continuity.
