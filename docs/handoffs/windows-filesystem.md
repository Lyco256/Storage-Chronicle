# feat/windows-filesystem handoff

## Scope

Implemented `Requirements/08_AGENT_WINDOWS_FILESYSTEM.md` within the assigned paths only:

- Windows-only `net10.0-windows10.0.19041.0` project with `EnableWindowsTargeting` for safe non-Windows compilation.
- Volume GUID enumeration, no-drive-letter volume retention, filesystem/drive/read-only/readability/USN capability mapping, and conservative ReFS capability detection.
- Event-driven Configuration Manager external-media arrival/removal notifications.
- P/Invoke-isolated volume, metadata, directory-handle, ReadDirectoryChangesW, and device-notification boundaries.
- Non-recursive initial metadata enumeration, reparse/junction/symbolic-link self-recording, access-denied `ExistenceOnly` fallback, pre-event exclusions, and `SnapshotBatchSize`-bounded streaming within a large directory.
- ReadDirectoryChangesW parser/monitor with rename pairing, malformed/buffer-overflow/handle-loss/removal continuity gaps, and no periodic full scan.
- Bounded initial-scan notification buffer and a collector that isolates volume failures/cancellation.
- Explicit-confirmation-only metadata/path reconciliation with user-declined gap reporting and no invented process/exact timestamps.
- Matching `docs/src/StorageChronicle.Platform.Windows.FileSystem/**` explanations and synthetic tests.

## Validation

Commands run from the repository root:

```text
dotnet build StorageChronicle.slnx --no-restore
tests/StorageChronicle.Platform.Windows.FileSystem.Tests/bin/Debug/net10.0-windows10.0.19041.0/StorageChronicle.Platform.Windows.FileSystem.Tests.exe --filter-not-trait Category=WindowsPrivileged --progress off
build/Test-Fast.ps1 -NoRestore
build/quality/Test-DocMirror.ps1
build/quality/Test-Architecture.ps1
build/quality/Test-Integration.ps1
build/quality/Test-Coverage.ps1
build/quality/Test-VirtualBoxTestLab.ps1
build/Test-Ui.ps1
```

Results:

- Integrated solution build: passed, 0 warnings, 0 errors.
- Windows filesystem tests: 33 passed, 0 failed, 0 skipped.
- Fast suite: 334 passed across 24 ordinary test projects; privileged-category cases excluded.
- Documentation mirror, architecture, integration, E2E, headless UI, coverage, VirtualBox safety-contract, and UI-specific gates: passed.
- Coverage thresholds: Domain 82.41%, State 94.13%, Projection 89.28%, Storage 87.69%; all required ViewModel thresholds passed.

The tests cover synthetic notification parsing and malformed buffers, buffer-overflow continuity gaps, drive-letterless volume GUIDs, ReFS fallback capability, event-driven media callbacks, exclusion boundaries, pinned parent/child handle ancestry, a real Windows directory-to-symlink swap between enumeration and open, configured subdirectory traversal from a volume GUID root, reparse non-recursion, access-denied metadata quality, user-declined/confirmed reconciliation, pipeline backpressure, and cancellation.

## Integration follow-up

- Integrated into the top-agent `devenv` worktree from feature commit `5da1f01c34c65aaff67721b628bb557fa8fc1cb8` using a reviewed `--no-ff` merge; the conflicted Windows filesystem hunks retained the newer integration-branch WMI/protected-volume behavior and were repaired/tested in place.
- Reparse resistance now applies to snapshot descendants and to both configured snapshot/monitor roots: each starts at the direct volume root and walks selected subdirectory components through pinned handles, stopping on a reparse or non-directory component.
- `build/Test-Fast.ps1` now excludes `Category=WindowsPrivileged` by default. Privileged acceptance cases were deliberately not run on the everyday host; no product service or physical-media workflow was started.

## Known integration notes

- The feature project/test project are intentionally not added to `StorageChronicle.slnx`; solution registration is a shared integration change owned by the top agent.
- Privileged Windows API tests requiring real device arrival/removal or protected volumes should be selected by the repository's Windows privileged test harness. The deterministic tests here use injected native boundaries.
- Configuration-history events for exclusion changes remain the settings owner's responsibility; this collector only applies the effective policy before source-event generation.
- The collector uses the existing shared event model without adding a new live-directory origin; live ReadDirectoryChangesW source facts are marked with `EventOrigin.DirectoryReconciliation` and a `source=ReadDirectoryChangesW` property. This was reviewed as an existing top-agent-owned integration seam; no duplicate shared contract was introduced.
