# SettingsDialogWindow.cs

## Role

Hosts the modal Machine/User settings editor in the desktop shell. All reads and writes go through `AgentPipeProjectionClient` and the canonical `IAgentSettingsGateway`; this view never opens settings files or performs storage I/O.

## Public types

- `SettingsDialogWindow`: builds the Avalonia modal, loads the validated snapshot asynchronously, parses the visible fields, invokes the shared settings ViewModel, and surfaces Agent/validation errors without reporting false success.

## Invariants and dependencies

- Machine changes are submitted with Machine scope and rely on Agent authorization and lifecycle handling.
- User changes retain all canonical settings, including Event Stack and Diff View preferences.
- Paths, media mirror pairs, enum names, and bounded numeric values are validated before applying.
- Requires Avalonia controls, `StorageChronicle.Settings`, `StorageChronicle.UI.Settings`, and the shared Agent pipe client.

## Failure behavior and tests

Unavailable IPC and malformed field input are shown in the dialog status; no direct settings-file fallback exists. Covered by `tests/StorageChronicle.UI.Headless.Tests/AgentPipeSettingsClientTests.cs` and `tests/StorageChronicle.UI.Settings.Tests/SettingsDialogViewModelTests.cs`; the Desktop project build validates Avalonia integration.
