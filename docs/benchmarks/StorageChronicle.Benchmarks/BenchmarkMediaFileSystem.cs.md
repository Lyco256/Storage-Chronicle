# BenchmarkMediaFileSystem.cs

## Role

Provides a volume-bound media filesystem for external-media benchmarks so benchmark writes exercise the same handle-confined contract used by production Windows media storage.

## Public types and responsibilities

`BenchmarkMediaFileSystem` is internal. `Open` resolves the supplied benchmark root and returns its `VolumeId` with an `IVolumeBoundMediaFileSystem` implementation. The portable implementation exists only for non-Windows benchmark builds.

## Invariants

Windows operations are rooted at an existing directory and pinned volume handle. The portable implementation confines relative paths beneath the supplied root, rejects traversal components, creates new files without overwriting, and permits moves only for streams created or opened for recovery by that instance. It never reads file contents except through the explicit media contract's `OpenRead` method.

## Dependencies

Uses `StorageChronicle.Contracts`, `StorageChronicle.Domain.Contracts`, and on Windows `WindowsVolumeDirectorySession` plus Win32 volume lookup APIs.

## Failure behavior

Invalid roots, unavailable Windows volume mappings, path escapes, existing destinations, and streams not owned by this instance fail with exceptions; no fallback fabricates volume identity.

## Relevant tests

Covered indirectly by `tests/StorageChronicle.ExternalMedia.Tests` and the Windows handle-boundary tests in `tests/StorageChronicle.Platform.Windows.FileSystem.Tests`.

## OS constraints

Windows builds require Windows volume APIs. Non-Windows builds use a confined portable fixture implementation and do not certify Windows handle semantics.
