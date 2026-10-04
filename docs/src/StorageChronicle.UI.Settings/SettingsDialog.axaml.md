# SettingsDialog.axaml

## Role

Avalonia modal settings view. It binds Machine and User Settings editors to `SettingsDialogViewModel`; it never opens settings files or performs Agent I/O directly.

## Public types and responsibilities

The view declares `SettingsDialogWindow`. The impact-review panel displays the complete old/new change set and write-boundary disclosure. Its Cancel Review and Confirm and Apply buttons are bound to explicit ViewModel commands; the ordinary Apply button cannot bypass a pending review.

## Inputs and outputs

Inputs and outputs are the declared contracts of the source file. File contents and file-content hashes are never an input or output.

## Dependencies

Depends on Avalonia controls and the `SettingsDialogViewModel` binding surface.

## Invariants

Machine-setting changes that alter monitoring, exclusions, collection behavior, history location, flush behavior, or media mirror scope/destination are not applied until the user reviews and confirms the displayed impact. User-only preference changes do not trigger a machine impact review.

## Threading and lifetime

Callers own cancellation and lifetime; asynchronous work must not outlive the owning pipeline or UI scope.

## Failure behavior

Failure, corruption, cancellation, and recovery remain observable and are not converted into a false successful observation.

## Tests

`tests/StorageChronicle.UI.Settings.Tests/SettingsDialogViewModelTests.cs` covers preview, explicit confirmation, cancellation, snapshot invalidation, and user-only apply. `tests/StorageChronicle.UI.Headless.Tests` covers the compiled dialog/view integration.

Validated by tests/StorageChronicle.Integration.Tests and the affected integration tests.

## OS constraints

Platform-neutral behavior remains portable; Windows-only APIs are isolated in the Windows platform projects.

## Change-sensitive contracts

Public names, serialized fields, persistence boundaries, and the mirrored path are compatibility-sensitive contracts.
