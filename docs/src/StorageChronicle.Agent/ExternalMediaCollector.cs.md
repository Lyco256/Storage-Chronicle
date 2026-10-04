# ExternalMediaCollector.cs

`WindowsExternalMediaCollector` adapts Windows device arrival/removal notifications into source facts. It creates mount sessions, records media quality and removal gaps, and imports external-media mirror history through the immutable media store and import ledger only after the explicit consent gate authorizes the exact live PC/media/volume/root binding.

The collector owns no file-content reads. It preserves source quality, media identity, mount sequence, and recovery information as properties for the normalizer and downstream projections. It forwards protected-role flags, classification completeness, and mount points into `MediaVolumeDescriptor`; unknown roles block mirror import, registration, and missing-history recovery. A legacy mirror path does not authorize I/O: the collector queues a local first-use approval and waits for a decision. Existing history is imported only when the PC-local grant explicitly allows import; future mirror startup and append also require an exact grant. Read-only, system, boot, recovery, and EFI media are rejected. On notification continuity loss, active mirror sessions are invalidated without flushing, active media state is cleared for re-enumeration, and the next observed mount is recorded with `UnverifiedGap` quality. Media import ledgers use the fixed per-PC CommonApplicationData product-history directory; there is no production caller-selected ledger root. An internal factory seam routes integration tests only to run-owned fixtures. Mirror import failures are isolated and do not terminate the Agent collector loop.

Public types are `IExternalMediaChangeSource`, `WindowsExternalMediaChangeSource`, and `WindowsExternalMediaCollector`. Dependencies are the Windows volume/notification adapters, machine settings, media-consent service, external-media contracts, and the platform-neutral source-event contract. Tests cover connection, removal, enumeration failure, notification-gap invalidation/re-enumeration, mirror policy, volume binding, missing-history recovery, consent refusal/approval, and import behavior in `tests/StorageChronicle.Agent.Tests` and `tests/StorageChronicle.ExternalMedia.Tests`.

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
