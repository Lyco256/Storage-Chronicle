# ExternalMediaContracts.cs

Defines the platform-neutral media mirror contracts: format/schema/projection versions, volume capability and mirror policy records, quality and recovery outcomes, import ledgers, media-only filters, monitoring exclusion registration, and the deterministic clock abstraction. `MediaVolumeDescriptor` uses the shared `ProtectedVolumeRoles` representation and carries live mount points, classification completeness, and a separate write-time identity-binding flag. The mirror policy rejects any unknown/protected role and also rejects a root whose current volume identity was not bound immediately before use.

Invariants: a mirror is never enabled for system, boot, recovery, or EFI volumes, nor when any such role is unclassified; a matching mount-point string alone is not sufficient to authorize writing; media quality remains explicit rather than being upgraded by a UI; the import ledger is keyed by immutable manifest/segment SHA-256 values; and no contract creates or changes a USN journal. Tests cover incomplete/unsafe role rejection, missing identity binding, quality classification, filtering, and mount-session clock behavior.

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
