# ExternalMediaCollector.cs

`WindowsExternalMediaCollector` adapts Windows device arrival/removal notifications into source facts. It creates mount sessions, records media quality and removal gaps, and imports only validated external-media mirror history through the immutable media store and import ledger.

The collector owns no file-content reads. It preserves source quality, media identity, mount sequence, and recovery information as properties for the normalizer and downstream projections. Read-only, system, boot, recovery, and EFI media are rejected by the media-store policy. Mirror import failures are isolated and do not terminate the Agent collector loop.

Public types are `IExternalMediaChangeSource`, `WindowsExternalMediaChangeSource`, and `WindowsExternalMediaCollector`. Dependencies are the Windows volume/notification adapters, machine settings, external-media contracts, and the platform-neutral source-event contract. Tests cover connection, removal, enumeration failure, mirror policy, and import behavior in `StorageChronicle.ExternalMedia.Tests` and Agent integration tests.

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
