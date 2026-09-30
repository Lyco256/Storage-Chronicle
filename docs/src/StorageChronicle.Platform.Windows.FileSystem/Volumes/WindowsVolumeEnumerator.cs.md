# WindowsVolumeEnumerator

Maps native volume records to the shared `VolumeDescriptor` contract. Volume GUID identity is retained even with no drive letter, mount points are deduplicated, external media is identified from drive type, and directory-unreadable volumes are reported with `IsDirectoryReadable=false` instead of silently discarded. The native adapter queries read-only `MSFT_Partition` role facts and the pure `WindowsVolumeRoleClassifier` binds exactly one partition by volume GUID/mount point, recognizing only known GPT/MBR layouts. Missing, ambiguous, unsupported, or failed queries carry `ProtectedVolumeRoles.Unknown` and an incomplete flag. Even a complete role classification does not set the separate write-time volume identity binding; physical media mirroring remains disabled until that check is implemented. `WindowsPlatformCapabilities` limits capabilities to Windows 10 19041 APIs and conservatively disables ReFS journal continuity.

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
