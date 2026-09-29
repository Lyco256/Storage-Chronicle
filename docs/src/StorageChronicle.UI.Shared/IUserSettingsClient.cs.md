# IUserSettingsClient.cs

## Role

Defines the UI boundary for loading and applying current-user preferences through the Agent rather than opening settings files from a view.

## Public types and responsibilities

`IUserSettingsClient` exposes asynchronous load and apply operations using the canonical `UserSettings` and `SettingsApplyResult` models from `StorageChronicle.Settings`.

## Inputs and outputs

Load returns the validated current-user settings snapshot. Apply accepts a complete proposed snapshot and returns the Agent's success, restart, and validation result. The contract carries settings only; it never carries file contents or hashes.

## Invariants

The client accepts only the current-user settings scope. It does not expose machine paths, open settings files, or bypass Agent validation and persistence.

## Dependencies

Depends on the platform-neutral settings model and the UI.Shared IPC layer.

## Failure behavior

IPC, cancellation, malformed payload, and Agent validation failures remain observable to the caller. An empty settings response is treated as invalid data rather than replaced with fabricated values.

## Threading and lifetime

Both operations are asynchronous and accept caller-owned cancellation. Implementations must not retain work beyond the caller's operation.

## Relevant tests

Covered by [AgentPipeSettingsClientTests.cs](../../../../tests/StorageChronicle.UI.Headless.Tests/AgentPipeSettingsClientTests.cs) and [EventStackViewModelTests.cs](../../../../tests/StorageChronicle.UI.EventStack.Tests/EventStackViewModelTests.cs).

## OS constraints

Platform-neutral API; the concrete Agent transport uses the Windows named pipe.

## Change-sensitive contracts

The interface method names, cancellation parameters, and settings DTOs are consumed by both Event Stack preferences and the Agent IPC adapter. Changes must update the shared IPC gateway, source mirror, and both cited test suites together.
