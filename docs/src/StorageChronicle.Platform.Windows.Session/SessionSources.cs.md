# SessionSources.cs

## Role

Defines the OS-neutral session-side records and small deterministic components used by native adapters and tests.

## Public types

`ISessionClock` makes timestamps injectable. Clipboard records, statuses, retry options, notification/reader interfaces, and `ClipboardIntentTracker` preserve only bounded path candidates. `SessionIpcGuard` checks SID/session/message boundaries. `ShareSnapshotDiffer` consumes the shared `ShareDescriptor` contract and emits `ShareChange`; it does not introduce a duplicate share DTO. Cloud enums, observations, capability detection, and `CloudPlaceholderStateClassifier` describe local placeholder transitions.

## Invariants and failure behavior

The tracker allows repeated paste confirmation for one generation and can clear older generations. Empty or locked clipboard reads do not become confirmed intents. Retry options reject unbounded delays. Share comparison is case-insensitive by share name and compares path, type, description, and permission values. Cloud classification never contacts a cloud service.

## Dependencies and tests

The file depends on Domain, Contracts, and Platform.Abstractions only. It has no Windows P/Invoke. `SessionSourceTests.cs` covers all deterministic behavior and injected clocks.

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
