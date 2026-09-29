# WindowsCloudPlaceholderReader.cs

## Role

Reads local `FileAttributes` flags and maps them to Hydrated, Dehydrated, or SourceUnknown placeholder observations.

## Boundary and invariants

The reader is local-only. It does not invoke a cloud API, retrieve cloud history, or access user identity. Offline, unpinned, and recall-on-data flags indicate Dehydrated; ordinary local attributes indicate Hydrated. A separate classifier marks metadata-only changes when the state is unchanged.

## Failure and tests

Unsupported platforms, missing paths, access denied, and I/O failures return bounded Unknown/Unsupported observations with quality `Unknown`. Tests cover capability denial, missing paths, hydration transitions, and metadata-only changes.

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
