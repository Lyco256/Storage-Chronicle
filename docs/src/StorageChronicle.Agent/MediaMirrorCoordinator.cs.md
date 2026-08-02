# MediaMirrorCoordinator.cs

This file owns the Agent-side optional external-media mirror writer. It registers `.StorageChronicle` with the same Windows exclusion policy used by live and snapshot collection, batches canonical metadata into immutable CRC32C/SHA-256 segments, and seals A/B manifests linked to the prior manifest and mount session.

The coordinator is driven by canonical events after primary durability. It never receives file contents or hashes, refuses read-only/system/boot/recovery/EFI media, recovers interrupted temporary writes, and isolates mirror I/O from primary recording. Removal and supervised restart flush the bounded batch. Tests cover policy rejection, segment/manifest recovery, batching, and removal sealing.
