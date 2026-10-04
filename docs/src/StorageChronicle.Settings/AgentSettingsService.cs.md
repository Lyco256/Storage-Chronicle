# AgentSettingsService.cs

## Role and public API

`AgentSettingsService` implements the Agent-side settings gateway. It validates, authorizes, persists, and records changes for machine settings and user preferences. Overloads accepting `ISettingsStore<UserSettings>` let authenticated IPC supply the store selected for the caller's profile; machine settings remain in their dedicated machine store.

## Invariants and dependencies

Production user-settings IPC never uses the LocalSystem process profile: an authenticated profile store is required. Ordinary machine updates preserve media-consent records; consent grant changes use a dedicated verified path. Machine updates coordinate restart/rollback through `IMonitoringLifecycle`, and changes are recorded through `ISettingsChangeHistory`. Dependencies are the settings stores, validator, authorizer, history, lifecycle, clock, and cancellation tokens.

## Failure behavior and tests

Invalid settings, authorization denial, cancellation, persistence/history errors, and restart failures are surfaced as failed results or exceptions according to the gateway contract; no missing profile falls back to another store. `tests/StorageChronicle.Settings.Tests` covers settings validation, history, and consent invariants. `tests/StorageChronicle.Agent.Tests/AuthenticatedUserSettingsRoutingTests.cs` covers SID-scoped store selection and payload spoof resistance. Actual Windows service/profile behavior remains unverified until acceptance testing.

## Public types and responsibilities

`AgentSettingsService` implements `IAgentSettingsGateway` and owns validation/persistence orchestration. The IPC server authenticates the caller and selects the per-user store; this service does not infer identity from a settings payload.

## Inputs and outputs

Inputs are typed machine/user settings and cancellation tokens. Outputs are load/apply results and recorded settings-change events; file contents and content hashes are outside this API.

## Threading and lifetime

Machine updates and consent persistence use the service's synchronization boundary. User-store operations are scoped to the supplied authenticated profile store and do not retain it for another caller.

## OS constraints

The service is platform-neutral. Windows profile discovery is isolated in the Agent resolver; settings data is written only through the supplied store.

## Change-sensitive contracts

Settings validation, consent preservation, authorization requirements, history semantics, and the authenticated-user store overloads are compatibility-sensitive.
