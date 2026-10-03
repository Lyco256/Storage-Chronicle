# AgentSettingsService.cs

The canonical `IAgentSettingsGateway` exposes asynchronous snapshot reads and apply calls. `AgentSettingsService` implements reads as cancellation-aware `ValueTask` operations; the UI uses the IPC-backed implementation so settings files remain Agent-owned. The service still validates, authorizes Machine settings, performs safe restart/rollback, and records history.

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

The `MachineSettings.MediaMirrorConsents` list is machine-scoped and included in change detection. Applying changed consent through the ordinary machine-settings gateway follows the existing authorized settings/lifecycle path; the dedicated consent-decision IPC must persist only a validated single grant and must not bypass its authenticated interactive-session check.
