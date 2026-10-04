# AgentProjectionService.cs

## Role and public API

`AgentProjectionService` adapts canonical Agent history to platform-neutral Event Stack and Diff View projections. Its diff and Activity Frame timeline APIs accept the authenticated caller's `UserSettings` so `PaneTimeoutSeconds` is resolved per interactive profile. The IPC dispatcher loads those preferences using the authenticated SID-bound settings resolver.

## Invariants and dependencies

Reads are bounded/paged where required; grouped projection preserves complete group boundaries, and timeline details expose event metadata only. Source facts remain distinct from grouping and interpretation. File contents and content hashes are never read. Compatibility/test overloads can use an explicitly injected settings store; production IPC supplies profile-scoped settings. Dependencies include the canonical storage engine, projection contracts, and settings model.

## Failure behavior and tests

Cancellation, invalid ranges, storage corruption, and unavailable authenticated settings remain observable; IPC rejects profile-resolution failures rather than applying another user's timeout. `tests/StorageChronicle.Agent.Tests/AgentProjectionServiceTests.cs` covers projection/timeline behavior, with `AuthenticatedUserSettingsRoutingTests.cs` covering per-user IPC routing. Windows profile-registry behavior remains for service acceptance.

## Public types and responsibilities

`AgentProjectionService` owns durable-history reads and projection preparation; UI rendering and interpretation remain in the UI layer.

## Inputs and outputs

Inputs are bounded time/page requests and caller-scoped preferences. Outputs contain canonical event metadata and derived grouping, never file contents or content hashes.

## Threading and lifetime

Asynchronous reads honor caller cancellation and do not retain per-request settings beyond the call.

## OS constraints

The service is platform-neutral; OS collection and registry profile lookup remain outside this projection component.

## Change-sensitive contracts

Projection paging, frame identifiers, and the authenticated-settings overloads are compatibility-sensitive IPC behavior.
