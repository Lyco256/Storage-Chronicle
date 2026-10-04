# SettingsDialogViewModel.cs

`LoadAsync` retrieves Machine and User settings through the asynchronous `IAgentSettingsGateway` boundary. Validation errors and Agent apply failures remain visible and are never represented as success; machine restart state is reported only after an accepted Agent result. The ViewModel has no path or filesystem access.

## ??

Provides the headless state and command flow for the modal settings dialog. It receives settings only through `IAgentSettingsGateway`; it does not open or write settings files.

## Public types

- `SettingsDialogViewModel` loads and validates both settings scopes, presents machine-setting impact, and calls the existing gateway.
- `SettingsImpactPreview` contains the exact changed values and the product write-boundary explanation that must be reviewed before applying machine settings.
- `SettingsImpactChange` describes one changed setting, its old and proposed values, and the affected monitoring, Agent, or product-write behavior.

## Invariants and dependencies

- The initial Apply command validates drafts and compares machine settings with the last loaded/applied baseline. If monitoring paths, exclusions, Agent behavior, log destination, flush behavior, or media mirror scope/destination changed, it displays the complete preview and performs no gateway call.
- Only `ConfirmImpactAndApplyCommand` applies the immutable machine and user snapshots represented by the displayed preview. Preview entries are exposed through a read-only collection. Editing either draft invalidates the preview; `CancelImpactPreviewCommand` discards it without applying.
- User Settings-only edits use the ordinary Apply path. Every Apply still uses `IAgentSettingsGateway`; UI code never writes the backing files directly.
- The preview names exact old/new paths and mirror media identifiers. It states that monitored source contents are not read/stored and distinguishes Agent-authorized product-owned history/configuration writes from monitored source data. For mirrors it explicitly requires the Agent to verify actual volume identity, allowed role, and product-owned target, and to reject writes when those checks fail; the preview does not claim to certify a target itself.
- Apply failure or exception leaves `StatusMessage` unset and sets `ErrorMessage`; success is reported only after both gateway scopes succeed.
- The view model depends on CommunityToolkit.Mvvm and `StorageChronicle.Settings`; it contains no platform-specific APIs or duplicated settings contracts.

## Failure behavior

`tests/StorageChronicle.UI.Settings.Tests/SettingsDialogViewModelTests.cs` ? Headless load???????????Agent ????????????????

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
