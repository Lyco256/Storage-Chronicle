# Settings integration handoff

## Top-agent continuation (2026-10-04)

- Integrated `feat/settings` commit `895e079` with a no-fast-forward merge. It adds `WindowsSettingsPathProvider.ForAuthenticatedUser` and `UserSettingsStore.ForAuthenticatedUser`, validating SID/path syntax and providing tests for per-profile separation and isolated corruption recovery. The factory does not authenticate SID or prove profile ownership; Agent IPC supplies only the authenticated pipe-token SID and OS-resolved matching profile, never IPC-provided values.
- Machine-setting impact review is now connected to the current asynchronous gateway and displayed in the modal. Machine changes show exact old/new values, monitoring/write boundaries, and mirror authorization conditions before either Agent settings request. Confirmation applies captured immutable snapshots; edits invalidate the preview; cancel performs no write. User-only changes use ordinary Apply.
- Agent-side selection now reads the matching Windows ProfileList entry without write access, routes settings snapshot/update and Pane-timeout projection through that SID's profile store, and registers an unscoped sentinel that refuses service-profile fallback. The dispatch-boundary regression test exercises two SIDs and hostile payload SID/path fields; actual named-pipe token/profile behavior still requires Windows service acceptance.
- Initial branch merge exposed stale synchronous UI paths; the integration retained the current async gateway/input validation and adapted the preview. The modal now renders the write boundary and exact changes, with explicit Confirm/Cancel Review controls. `SettingsDialogViewModelTests` covers review, immutable confirmation, edit invalidation, cancellation, and user-only apply.
- Validation after integration: `build/Test-Fast.ps1 -NoRestore` passed all 24 fast validation projects with zero build warnings/errors; Settings UI tests 16/16 and Headless UI tests 9/9 passed. Expected fail-closed fixture diagnostics are emitted by negative validator tests; they are asserted and all test projects pass.
- Per-user routing Agent tests passed 75 total (72 pass, 3 privileged tests skipped because the elevated acceptance root is not configured). `build/Test-Fast.ps1 -NoRestore` then passed all 24 configured validation/test projects with exit code 0 and zero build warnings/errors. The new synthetic identity test covers dispatch routing after the authentication boundary, not a live multi-user token session; Windows service acceptance remains outstanding.

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
