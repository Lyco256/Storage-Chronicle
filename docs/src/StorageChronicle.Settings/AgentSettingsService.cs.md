# AgentSettingsService.cs

The canonical `IAgentSettingsGateway` exposes asynchronous snapshot reads and apply calls. `AgentSettingsService` implements reads as cancellation-aware `ValueTask` operations; the UI uses the IPC-backed implementation so settings files remain Agent-owned. The service validates and authorizes ordinary Machine settings, performs safe restart/rollback, and records history. A semaphore serializes machine-setting updates with the consent-only `GrantMediaMirrorConsentAsync` operation, which replaces one PC/media binding, validates and saves it, records a settings-history fact, and restores the prior settings if history recording fails. Consent persistence does not restart monitoring and cannot apply arbitrary settings.

## ??

UI ???????? Agent ? IPC ??????????????????????????????????????????????

## ????????

`IAgentSettingsGateway` ? UI ???????????UI ? Settings store ????????????????Machine ??? `IAgentSettingsAuthorizer` ????????????????? `SettingsChangeHistoryEvent` ??????????????? Machine ??? `IMonitoringLifecycle.RestartAsync` ??????

## ?????????

Store???????lifecycle ???????????????????? `SettingsApplyResult.Succeeded == false` ???UI ????????????????????????????????????????????????????????

## ?????

Settings ???? IPC ???????????????????????

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

The `MachineSettings.MediaMirrorConsents` list is PC-local and included in change detection. The dedicated IPC decision path must authenticate a current interactive Desktop UI caller; Session Agent clients cannot submit consent decisions. The settings service's narrow grant method is called only after the Agent revalidates the pending request and live media/root identities. Cancellation, invalid settings, storage errors, and history-recording failures remain visible and fail closed. `tests/StorageChronicle.Settings.Tests` and `tests/StorageChronicle.Agent.Tests` cover settings behavior and consent persistence.
