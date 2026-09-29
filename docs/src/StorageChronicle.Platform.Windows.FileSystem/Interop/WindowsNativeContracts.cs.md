# WindowsNativeContracts

Contains injectable boundaries for volume enumeration, read-only partition-role facts, metadata-only file/directory handles, ReadDirectoryChangesW, and Configuration Manager notifications. `NativeVolumeRecord` carries one shared `ProtectedVolumeRoles` value plus a completeness bit; `NativePartitionRoleRecord` contains read-only access paths, system/boot flags, and GPT/MBR type facts. No file contents or hashes are represented. Fakes exercise the classifier and higher layers without privileged Win32 state. The metadata boundary exposes a separate handle-opening operation so reconciliation can attempt a low file-I/O priority hint for every candidate kind.

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
