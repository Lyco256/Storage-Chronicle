# SettingsModels.cs

## Event Stack mode compatibility

`EventStackInitialMode` is the canonical persisted initial mode contract. `Source`, `Grouped`, and `Normalized` correspond to the Event Stack UI modes. Numeric values 0, 1, and 2 retain compatibility with legacy `Timeline`, `ActivityGroup`, and `File` values; the legacy activity/file modes open the current Grouped view. `Normalized` is additive at value 3. The default is Grouped. User settings stay in the versioned settings document and are applied through the Agent IPC boundary.

## ??

Machine Settings ? User Settings ? versioned JSON payload ??????????????????????Agent ????????????????????????????????????

## ????????

- `MachineSettings` ?????????????????????????????????1?60 ?? flush ??????
- `UserSettings` ? Activity Group/Pane timeout?Event Stack ????/??/??????Diff View ?????/???/????????????????
- `SettingsValidationResult` ??????????????`SettingsLoadResult<T>` ??????????????
- `SettingsChangeHistoryEvent` ??????????????????????

## ?????????

???????????? `SettingsPersistence.cs`???? `SettingsValidation.cs` ????????????????????????????/Agent ?????????????

## ?????

`tests/StorageChronicle.Settings.Tests/SettingsTests.cs` ????????????????Agent ????????

## Role

This mirror documents the source boundary for this file and explains how it participates in Storage Chronicle.

`MachineSettings.MediaMirrorConsents` is the canonical PC-local persistence contract for explicit media consent. Each `MediaMirrorConsentSettings` entry binds PC identity, logical media ID, live volume identity, dedicated root identity, filesystem and ACL-protection classifications, and approval time. A legacy `MediaMirrors` path does not imply consent.

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
