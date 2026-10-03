# WindowsDirectoryChangeMonitorFactory

Supplies the monitor with separate metadata/handle and notification native interfaces. Its production creation overload carries the direct volume root and validated relative root components so the monitor opens the selected directory through pinned handles instead of resolving configured subdirectory ancestors by path. The path-only overload remains for isolated direct-monitor tests and explicit roots.

## Role

This mirror documents the source boundary for this file and explains how it participates in Storage Chronicle.

## Public types and responsibilities

`IWindowsDirectoryChangeMonitorFactory` creates a monitor either from a direct root path or from a display path plus volume root and root components. `WindowsDirectoryChangeMonitorFactory` binds injectable native interfaces to that contract.

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

Validated by `tests/StorageChronicle.Platform.Windows.FileSystem.Tests/CollectorPipelineTests.cs` and `ReadDirectoryChangesTests.cs`.

## OS constraints

Platform-neutral behavior remains portable; Windows-only APIs are isolated in the Windows platform projects.

## Change-sensitive contracts

Public names, serialized fields, persistence boundaries, and the mirrored path are compatibility-sensitive contracts.
