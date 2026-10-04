# MediaMirrorCoordinator.cs

This file owns the Agent-side optional external-media mirror writer. It registers `.StorageChronicle` with the same Windows exclusion policy used by live and snapshot collection, batches canonical metadata into immutable CRC32C/SHA-256 segments, and seals A/B manifests linked to the prior manifest and mount session. Before opening the store it requires a persisted exact PC/media/volume/root consent binding from `MediaMirrorConsentService`; startup uses `createIfMissing: false` and rechecks the root identity from the volume-bound handle.

The coordinator is driven by canonical events after primary durability. It never receives file contents or hashes, refuses read-only/system/boot/recovery/EFI media and incomplete role classification, and does not treat a matching mount-point string as identity evidence. Missing, changed, or unavailable consent causes startup to fail closed. Existing roots must pass the volume-bound ownership validation; unknown pre-existing `.StorageChronicle` data is not adopted or modified. Mirror I/O remains isolated from primary recording. On notification continuity loss, `InvalidateAsync` drops the uncertain session without flushing to a potentially removed/replaced medium; ordinary removal still uses `UnregisterAsync` to flush and seal. Tests cover policy rejection, identity binding, ownership, segment/manifest recovery, batching, removal sealing, continuity invalidation, and consent denial.

## Dependencies and lifetime

The coordinator uses machine settings, the consent service, the handle-bound media filesystem, the external-media store, and the shared exclusion registrar. It owns one filesystem/store session per mounted volume and flushes/seals it on removal or shutdown.

## Failure behavior and tests

Consent mismatch, volume I/O failure, manifest corruption, cancellation, and append failure are surfaced to the caller and must not silently create or adopt a root. Tests are in `tests/StorageChronicle.Agent.Tests` and `tests/StorageChronicle.ExternalMedia.Tests`.

## Role

This mirror documents the source boundary for this file and explains how it participates in Storage Chronicle.

## Public types and responsibilities

Public types preserve source facts and the explicitly owned responsibility; UI interpretation and correlation remain outside this boundary.

## Inputs and outputs

Inputs and outputs are the declared contracts of the source file. File contents and file-content hashes are never an input or output.

## Dependencies

Dependencies are limited to the referenced project contracts and platform services shown by the source file.

## Invariants

The source keeps canonical facts distinguishable from reconstructed state and does not synthesize descendant events.

## Threading and lifetime

Callers own cancellation and lifetime; asynchronous work must not outlive the owning pipeline or UI scope.

## Failure behavior

Failure, corruption, cancellation, and recovery remain observable and are not converted into a false successful observation.

## Tests

Validated by tests/StorageChronicle.Integration.Tests and the affected integration tests.

## OS constraints

Platform-neutral behavior remains portable; Windows-only APIs are isolated in the Windows platform projects.

## Change-sensitive contracts

Public names, serialized fields, persistence boundaries, and the mirrored path are compatibility-sensitive contracts.
