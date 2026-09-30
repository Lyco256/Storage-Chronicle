# SettingsDialogViewModel.cs

## Role

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

Invalid drafts and attempts to apply before loading are rejected before gateway access. Agent rejection and thrown exceptions are surfaced as errors, never success. A failed user-scope apply after machine apply is surfaced as a partial failure; the machine baseline is updated to reflect the successfully applied machine settings.

## Relevant tests

`tests/StorageChronicle.UI.Settings.Tests/SettingsDialogViewModelTests.cs` covers load/recovery warnings, validation, machine impact preview without premature gateway access, invalidation and exact reviewed-snapshot application, cancellation, ordinary User Settings, returned and thrown Agent failures, and success status.
