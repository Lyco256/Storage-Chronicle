# IUserSettingsClient.cs

## Role

Defines the UI boundary for loading and applying current-user preferences through the Agent rather than opening settings files from a view.

## Public types and responsibilities

`IUserSettingsClient` exposes asynchronous load and apply operations using the canonical `UserSettings` and `SettingsApplyResult` models from `StorageChronicle.Settings`.

## Invariants

The client accepts only the current-user settings scope. It does not expose machine paths, open settings files, or bypass Agent validation and persistence.

## Dependencies

Depends on the platform-neutral settings model and the UI.Shared IPC layer.

## Failure behavior

IPC, cancellation, malformed payload, and Agent validation failures remain observable to the caller. An empty settings response is treated as invalid data rather than replaced with fabricated values.

## Relevant tests

Covered by UI.Shared/Agent IPC contract tests and Event Stack preference tests.

## OS constraints

Platform-neutral API; the concrete Agent transport uses the Windows named pipe.
