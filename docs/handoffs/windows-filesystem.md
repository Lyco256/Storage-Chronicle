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

## Reparse-point hardening follow-up

Snapshot traversal now opens each traversable directory with `FILE_FLAG_OPEN_REPARSE_POINT`, verifies the opened handle is a directory and not a reparse point, reads the current-directory metadata through that same handle, and enumerates direct children with `GetFileInformationByHandleEx` against that handle. The snapshot no longer uses `Directory.EnumerateFileSystemEntries(path)`. Child and parent metadata path opens use `FILE_FLAG_OPEN_REPARSE_POINT`, so a child that is swapped to a final-component symlink/junction before its metadata/open step is observed as that reparse object or rejected; it is not traversed. The monitor shares the hardened `OpenDirectory` boundary and reports a gap if the requested root is a final-component reparse point. Volume-GUID identifiers resolve to the direct volume root rather than the first mount-point alias.

This is a precise but not complete path-containment guarantee. Windows still resolves ancestor components of a full child path. If a parent path component is replaced with a reparse point after the parent handle was opened, path-based child metadata/open calls may resolve through that ancestor. The active directory enumeration itself remains bound to its open handle, but closing this ancestor race requires a native relative-to-parent-handle open/metadata contract (for example, a reviewed handle-relative design); no substitute shared contract was added here. If a directory path changes after its handle is opened, enumeration continues on the originally opened directory object, not the replacement at that path.

### Follow-up validation

- `dotnet test tests/StorageChronicle.Platform.Windows.FileSystem.Tests/StorageChronicle.Platform.Windows.FileSystem.Tests.csproj`: **17 passed, 0 failed, 0 skipped**. This includes actual Windows temporary-directory symbolic-link cases for both monitor-root rejection and snapshot link-self recording/no-target traversal, plus a simulated queued-directory replacement fail-closed case. No physical media was used.
- `build/Test-Fast.ps1`: **passed**; its current solution registration ran the 2 Domain tests only. The Windows FileSystem project was separately tested using its project file above.
- `git diff --check`: **passed** (only Git's configured LF-to-CRLF notices were emitted).
- No DocMirror validation script exists under `build/`; a path-by-path check confirmed every changed `src/**/*.cs` file has its `docs/src/**/*.cs.md` mirror, including the new volume-root helper.
- The repository pins SDK `10.0.302`, which is not installed on this host. To avoid changing `global.json`, validation was launched from `C:\Windows\Temp`, selecting installed SDK `10.0.401`; project targeting remains `net10.0-windows10.0.19041.0`.
