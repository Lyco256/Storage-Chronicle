# feat/windows-filesystem handoff

## Scope

Implemented `Requirements/08_AGENT_WINDOWS_FILESYSTEM.md` within the assigned paths only:

- Windows-only `net10.0-windows10.0.19041.0` project with `EnableWindowsTargeting` for safe non-Windows compilation.
- Volume GUID enumeration, no-drive-letter volume retention, filesystem/drive/read-only/readability/USN capability mapping, and conservative ReFS capability detection.
- Event-driven Configuration Manager external-media arrival/removal notifications.
- P/Invoke-isolated volume, metadata, directory-handle, ReadDirectoryChangesW, and device-notification boundaries.
- Non-recursive initial metadata enumeration, reparse/junction/symbolic-link self-recording, access-denied `ExistenceOnly` fallback, and pre-event exclusions.
- ReadDirectoryChangesW parser/monitor with rename pairing, malformed/buffer-overflow/handle-loss/removal continuity gaps, and no periodic full scan.
- Bounded initial-scan notification buffer and a collector that isolates volume failures/cancellation.
- Explicit-confirmation-only metadata/path reconciliation with user-declined gap reporting and no invented process/exact timestamps.
- Matching `docs/src/StorageChronicle.Platform.Windows.FileSystem/**` explanations and synthetic tests.

## Validation

Commands run from the repository root:

```text
dotnet build src/StorageChronicle.Platform.Windows.FileSystem/StorageChronicle.Platform.Windows.FileSystem.csproj
dotnet test tests/StorageChronicle.Platform.Windows.FileSystem.Tests/StorageChronicle.Platform.Windows.FileSystem.Tests.csproj
```

Results:

- Project build: passed, 0 warnings, 0 errors.
- Tests: 12 passed, 0 failed, 0 skipped.

The tests cover synthetic notification parsing and malformed buffers, buffer-overflow continuity gaps, drive-letterless volume GUIDs, ReFS fallback capability, event-driven media callbacks, exclusion boundaries, reparse non-recursion, access-denied metadata quality, user-declined/confirmed reconciliation, and cancellation.

## Known integration notes

- The feature project/test project are intentionally not added to `StorageChronicle.slnx`; solution registration is a shared integration change owned by the top agent.
- Privileged Windows API tests requiring real device arrival/removal or protected volumes should be selected by the repository's Windows privileged test harness. The deterministic tests here use injected native boundaries.
- Configuration-history events for exclusion changes remain the settings owner's responsibility; this collector only applies the effective policy before source-event generation.
- The collector uses the existing shared event model without adding a new live-directory origin; live ReadDirectoryChangesW source facts are marked with `EventOrigin.DirectoryReconciliation` and a `source=ReadDirectoryChangesW` property pending any top-agent-approved shared-contract evolution.
