# SessionSources.cs

## Role

Defines the OS-neutral session-side records and small deterministic components used by native adapters and tests.

## Public types

`ISessionClock` makes timestamps injectable. Clipboard records, statuses, retry options, notification/reader interfaces, and `ClipboardIntentTracker` preserve only bounded path candidates. `SessionIpcGuard` checks SID/session/message boundaries. `ShareSnapshotDiffer` consumes the shared `ShareDescriptor` contract and emits `ShareChange`; it does not introduce a duplicate share DTO. Cloud enums, observations, capability detection, and `CloudPlaceholderStateClassifier` describe local placeholder transitions.

## Invariants and failure behavior

The tracker allows repeated paste confirmation for one generation and can clear older generations. Empty or locked clipboard reads do not become confirmed intents. Retry options reject unbounded delays. Share comparison is case-insensitive by share name and compares path, type, description, and permission values. Cloud classification never contacts a cloud service.

## Dependencies and tests

The file depends on Domain, Contracts, and Platform.Abstractions only. It has no Windows P/Invoke. `SessionSourceTests.cs` covers all deterministic behavior and injected clocks.
