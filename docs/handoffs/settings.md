# Settings integration handoff

## Scope and ownership

- Requirement: `Requirements/09_AGENT_SETTINGS.md`.
- Integrated paths: `src/StorageChronicle.Settings/**`, `src/StorageChronicle.UI.Settings/**`, `src/StorageChronicle.UI.Shared/AgentPipeSettingsGateway.cs`, and the corresponding tests and mirrors.
- Shared IPC transport was extended only by the top agent through `AgentPipeProjectionClient.SendRequestAsync`; no duplicate settings contract was introduced.

## Implemented behavior

- Machine and user settings load through the Agent gateway, with recovery warnings surfaced in the modal.
- The UI edits monitoring/exclusion paths, durable log path, media mirror mappings, flush interval, Event Stack, Diff View, zoom, ordering, and saved filters.
- Apply validates locally, sends machine settings before user settings, reports Agent failures, handles cancellation, and closes the modal only after success.
- Machine restart-required state and pending restart status remain visible; settings history stays in the Agent/persistence boundary.
- Avalonia headless coverage includes rendering, recovery warning, draft editing, success close, failure retention, Cancel, and in-flight cancellation.

## Verification

- `dotnet build src/StorageChronicle.Settings/StorageChronicle.Settings.csproj --no-restore`: passed, 0 warnings/errors.
- `dotnet build src/StorageChronicle.UI.Settings/StorageChronicle.UI.Settings.csproj --no-restore`: passed, 0 warnings/errors.
- `dotnet test tests/StorageChronicle.Settings.Tests/StorageChronicle.Settings.Tests.csproj --no-restore`: passed.
- `dotnet test tests/StorageChronicle.UI.Settings.Tests/StorageChronicle.UI.Settings.Tests.csproj --no-restore`: 13 passed, 0 failed, 0 skipped.
- The critical coverage gate reports `SettingsDialogViewModel` at 93.41%.
- `dotnet run --project tools/StorageChronicle.DocMirrorValidator --no-restore -- .`: passed.

## Known limitations and top-agent checks

- Physical Agent IPC, service identity, non-admin authorization, and settings migration must be exercised in the Windows installer/service acceptance matrix before release.
- The top agent must review the named-pipe serialization against the versioned runtime contract and include settings history in the final recovery test.
- No shared contract request remains open from this handoff.
