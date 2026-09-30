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

## Bounded pipeline handoff follow-up

The source-event output queue and each volume's `nativeReads`/`liveReads` handoff now use `BoundedChannelFullMode.Wait` with the validated `PipelineChannelCapacity` limit. `WindowsDirectoryChangeMonitor` also bounds its producer/reader handoff to one result. A full channel therefore applies backpressure and does not itself discard a source fact. Backpressure reaches the native monitor; if its Windows notification buffer overflows, `ERROR_NOTIFY_ENUM_DIR` becomes an explicit `UnverifiedGap`. Independently, the bounded initial-scan retention buffer continues to preserve accepted notifications and reports an explicit gap when its own capacity is exceeded. Failure-gap writes use the linked cancellation token, and early disposal cancels and awaits the monitor producer so a blocked write/read cannot hold shutdown open.

`WindowsExternalMediaMonitor` now has a bounded ordinary-event queue (default capacity 256, allowed 1–65,536) plus one physically reserved slot for `ExternalMediaChangeKind.RescanRequired`. Its native callback performs a short serialized queue/state transition and uses non-waiting `TryWrite`; it never awaits or waits for channel capacity. When normal capacity is full it records one rescan marker and coalesces later arrival/removal callbacks until the marker is consumed. The marker means individual device changes may have been lost, so the consumer must reconcile the current volume set. Reading the marker clears overflow state and subsequent events are accepted again. Reader cancellation does not drain or discard queued records; a later reader can continue. The rescan state uses the existing Requirement 08-owned `ExternalMediaChangeKind` contract; no duplicate shared contract was introduced.

Native `ReadDirectoryChangesW` now runs on an overlapped directory handle. Cancellation targets that read's exact `OVERLAPPED` via `CancelIoEx`, and the pinned buffer/event/native operation state is retained until completion, avoiding the prior detached synchronous `Task.Run` read and use-after-free/hung-shutdown risk. A Windows temporary-directory test verifies prompt cancellation; it does not enumerate or access physical/removable devices.

Validation for this follow-up:

- `dotnet test "C:\Users\lyco2\.codex\worktrees\filesystem-reparse-hardening\Storage Chronicle\tests\StorageChronicle.Platform.Windows.FileSystem.Tests\StorageChronicle.Platform.Windows.FileSystem.Tests.csproj" --no-restore` (launched from `C:\Windows\Temp`): **23 passed, 0 failed, 0 skipped**. Capacity-one tests cover ordered snapshot/live delivery with a native overflow gap, per-volume isolation after one volume's overflow, and early consumer disposal while bounded output can be blocked. External-media tests cover normal capacity, reserved overflow marker/coalescing, callback return under overflow, recovery after marker consumption, and cancellation followed by preservation/draining of queued events. A Windows-only temporary-directory test confirms prompt cancellation of pending overlapped ReadDirectoryChangesW I/O. Existing tests cover initial-scan retention overflow and monitor overflow translation. No physical/removable device was accessed.
- `powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\lyco2\.codex\worktrees\filesystem-reparse-hardening\Storage Chronicle\build\Test-Fast.ps1" -NoRestore` (launched from `C:\Windows\Temp`): **passed**, 2 Domain tests. The Windows FileSystem project is separately validated by the project command above because it is intentionally not registered in the shared solution.
- `git diff --check`: **passed** (Git emitted configured LF-to-CRLF notices only). A path-by-path script confirmed matching `docs/src/**/*.cs.md` files for all 5 changed source files, including the overlapped-I/O native implementation.
- The repository pins SDK `10.0.302`, which is not installed on this host; launching from `C:\Windows\Temp` selects installed SDK `10.0.401` without changing `global.json`.

The path-based ancestor-component reparse race described above remains open; it has not been closed by this channel change. It blocks claiming a complete physical pre-execution containment audit for affected traversal unless/until the race is closed by an approved relative-handle design or the top agent explicitly assesses and accepts the residual risk. This delegated branch cannot authorize that acceptance. No physical workload was run.
