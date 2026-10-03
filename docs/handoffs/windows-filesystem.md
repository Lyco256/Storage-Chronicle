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

Snapshot traversal opens its root with `FILE_FLAG_OPEN_REPARSE_POINT`, verifies the handle is a directory and not a reparse point, reads root metadata from that handle, and enumerates direct children with `GetFileInformationByHandleEx` against that same handle. Descendants are opened with `NtCreateFile` relative to the pinned parent handle, with a validated single-component name and `FILE_OPEN_REPARSE_POINT`; metadata and parent identity are then read from those handles. Directory handles are carried into recursion rather than reopened by path. The monitor shares the hardened `OpenDirectory` boundary and reports a gap if the requested root is a final-component reparse point. Volume-GUID identifiers resolve to the direct volume root rather than the first mount-point alias.

Snapshot descendant acquisition no longer resolves descendant ancestor paths: it is anchored to the already-open parent directory handle. The monitor's root remains path-opened by design and is separately verified as a non-reparse directory. If a directory path changes after its handle is opened, enumeration and descendant acquisition continue on the originally opened directory object, not the replacement at that path.

### Follow-up validation

- `dotnet test tests/StorageChronicle.Platform.Windows.FileSystem.Tests/StorageChronicle.Platform.Windows.FileSystem.Tests.csproj`: **17 passed, 0 failed, 0 skipped**. This includes actual Windows temporary-directory symbolic-link cases for both monitor-root rejection and snapshot link-self recording/no-target traversal, plus a simulated queued-directory replacement fail-closed case. No physical media was used.
- `build/Test-Fast.ps1`: **passed**; its current solution registration ran the 2 Domain tests only. The Windows FileSystem project was separately tested using its project file above.
- `git diff --check`: **passed** (only Git's configured LF-to-CRLF notices were emitted).
- No DocMirror validation script exists under `build/`; a path-by-path check confirmed every changed `src/**/*.cs` file has its `docs/src/**/*.cs.md` mirror, including the new volume-root helper.
- The repository pins SDK `10.0.302`, which is not installed on this host. To avoid changing `global.json`, validation was launched from `C:\Windows\Temp`, selecting installed SDK `10.0.401`; project targeting remains `net10.0-windows10.0.19041.0`.

## Bounded pipeline handoff follow-up

The source-event output queue and each volume's `nativeReads`/`liveReads` handoff now use `BoundedChannelFullMode.Wait` with the validated `PipelineChannelCapacity` limit. `WindowsDirectoryChangeMonitor` also bounds its producer/reader handoff to one result. A full channel therefore applies backpressure and does not itself discard a source fact. Backpressure reaches the native monitor; if its Windows notification buffer overflows, `ERROR_NOTIFY_ENUM_DIR` becomes an explicit `UnverifiedGap`. Independently, the bounded initial-scan retention buffer continues to preserve accepted notifications and reports an explicit gap when its own capacity is exceeded. Failure-gap writes use the linked cancellation token, and early disposal cancels and awaits the monitor producer so a blocked write/read cannot hold shutdown open.

`WindowsExternalMediaMonitor` now has a bounded ordinary-event queue (default capacity 256, allowed 1–65,536) plus one physically reserved slot for `ExternalMediaChangeKind.RescanRequired`. Its native callback performs a short serialized queue/state transition and uses non-waiting `TryWrite`; it never awaits or waits for channel capacity. When normal capacity is full it records one rescan marker and coalesces later arrival/removal callbacks until the marker is consumed. The marker means individual device changes may have been lost, so the consumer must reconcile the current volume set. Reading the marker clears overflow state and subsequent events are accepted again. Reader cancellation does not drain or discard queued records; a later reader can continue. The rescan state uses the existing Requirement 08-owned `ExternalMediaChangeKind` contract; no duplicate shared contract was introduced.

Native `ReadDirectoryChangesW` now runs on an overlapped directory handle. Cancellation targets that read's exact `OVERLAPPED` via `CancelIoEx`, and the pinned buffer/event/native operation state is retained until completion, avoiding the prior detached synchronous `Task.Run` read and use-after-free/hung-shutdown risk. A Windows temporary-directory test verifies prompt cancellation; it does not enumerate or access physical/removable devices.

Validation for this follow-up:

- `dotnet test "C:\Users\lyco2\.codex\worktrees\filesystem-reparse-hardening\Storage Chronicle\tests\StorageChronicle.Platform.Windows.FileSystem.Tests\StorageChronicle.Platform.Windows.FileSystem.Tests.csproj" --no-restore` (launched from `C:\Windows\Temp`): **23 passed, 0 failed, 0 skipped**. Capacity-one tests cover ordered snapshot/live delivery with a native overflow gap, per-volume isolation after one volume's overflow, and early consumer disposal while bounded output can be blocked. External-media tests cover normal capacity, reserved overflow marker/coalescing, callback return under overflow, recovery after marker consumption, and cancellation followed by preservation/draining of queued events. A Windows-only temporary-directory test confirms prompt cancellation of pending overlapped ReadDirectoryChangesW I/O. Existing tests cover initial-scan retention overflow and monitor overflow translation. No physical/removable device was accessed.
- `powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\lyco2\.codex\worktrees\filesystem-reparse-hardening\Storage Chronicle\build\Test-Fast.ps1" -NoRestore` (launched from `C:\Windows\Temp`): **passed**, 2 Domain tests. The Windows FileSystem project is separately validated by the project command above because it is intentionally not registered in the shared solution.
- `git diff --check`: **passed** (Git emitted configured LF-to-CRLF notices only). A path-by-path script confirmed matching `docs/src/**/*.cs.md` files for all 5 changed source files, including the overlapped-I/O native implementation.
- The repository pins SDK `10.0.302`, which is not installed on this host; launching from `C:\Windows\Temp` selects installed SDK `10.0.401` without changing `global.json`.

The ancestor-component race statement above was accurate when the bounded-pipeline follow-up was written. It is superseded for snapshot descendant traversal by the 2026-10-03 handle-relative follow-up below. That later change closes the specific snapshot race; it does not authorize physical product execution or claim the separate full source-to-sink audit complete. No physical workload was run.

## Handle-relative snapshot containment follow-up (2026-10-03)

The remaining path-based snapshot descendant race was closed within the Requirement 08-owned native boundary. `IWindowsFileMetadataNative` now exposes `OpenChild` and `ReadMetadataRelative`. The Windows implementation uses `NtCreateFile` with `OBJECT_ATTRIBUTES.RootDirectory` bound to the parent handle and `FILE_OPEN_REPARSE_POINT`; names with separators, alternate streams, dot components, NUL, or excessive component length are rejected. The opened handle's actual directory bit must match the enumerated class. Child files request only metadata/synchronization access, never file-data access. Parent FileId is read directly from the pinned parent handle. Snapshot recursion retains each opened directory handle and no longer reopens descendants by path. Output and gap batches flush at the configured bound. Open failures and stale-entry/type mismatches become explicit bounded gap events; no path-based retry is performed.

Added a Windows API test which replaces an enumerated empty directory with a symbolic link to a separate temporary target immediately before the reader opens that entry. It verifies that the link itself is recorded as a reparse point and that the target's `outside.txt` entry is not observed. The test uses only a temporary directory and file fixture; it does not touch product state or physical media. A separate fake-native test verifies that each descendant open is associated with the correct pinned parent handle and that snapshot metadata does not use path-only APIs.

Validation from `C:\Windows\Temp` to select installed SDK 10.0.401 (repository-pinned 10.0.302 is absent; `global.json` was not changed):

- `dotnet test "C:\Users\lyco2\.codex\worktrees\filesystem-reparse-hardening\Storage Chronicle\tests\StorageChronicle.Platform.Windows.FileSystem.Tests\StorageChronicle.Platform.Windows.FileSystem.Tests.csproj" --no-restore --verbosity minimal`: **24 passed, 0 failed, 0 skipped** on Windows, including the actual temporary-directory reparse-swap test.
- `powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\lyco2\.codex\worktrees\filesystem-reparse-hardening\Storage Chronicle\build\Test-Fast.ps1" -NoRestore`: **passed**, 2 Domain tests. The Windows filesystem project is not registered in the shared solution and is validated separately above.
- `git diff --check`: passed; Git emitted only configured LF-to-CRLF notices.
- Mirrored documentation updated for all three changed source files; full DocMirror and integrated solution validation remain the integration gate.

This closes the specific snapshot descendant ancestor-reparse race only. It does not constitute the commit-bound full source-to-sink write audit, independent process-attributed write monitoring, external-media ownership/ancestor-ACL audit, privileged preflight, physical-media acceptance, Windows 10 22H2 acceptance, or performance acceptance. Do not start the product service or touch physical media based on this change alone.
