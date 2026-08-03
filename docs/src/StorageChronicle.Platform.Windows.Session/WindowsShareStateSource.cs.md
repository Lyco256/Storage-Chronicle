# WindowsShareStateSource.cs

## Role

Implements the shared SMB share source: startup snapshot plus event-driven change snapshots and deterministic diffs.

## Inputs and outputs

`IShareSnapshotReader` supplies local `ShareDescriptor` values. `IShareChangeNotifier` waits for LanmanServer registry changes. The source emits `SourceEvent` values with `ShareChange` origin, `ShareChanged` hint, share properties, and a quality marker of `RegistryNotification` or `FallbackPolling`.

## Invariants and failure behavior

The initial snapshot is read once. Registry notification is preferred; when registration is unavailable or fails, the source waits 30 seconds between `NetShareEnum` snapshots. Share changes remain separate from file metadata and contain no remote-user or remote-PC data. Cancellation interrupts waits and snapshot reads.

## Tests

Owned tests inject snapshot/notifier fakes to verify path, description, permission changes, dedicated share origin, exact notification quality, and fallback quality without changing the shared contract.

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
