# Windows session/share handoff

## Assigned requirement

`Requirements/14_AGENT_WINDOWS_SESSION_AND_SHARE.md` on branch `feat/windows-session-share`.

## Owned paths

- `src/StorageChronicle.Platform.Windows.Session/**`
- `src/StorageChronicle.SessionAgent/**`
- `tests/StorageChronicle.Platform.Windows.Session.Tests/**`
- `docs/src/StorageChronicle.Platform.Windows.Session/**`
- `docs/src/StorageChronicle.SessionAgent/**`
- `docs/handoffs/windows-session-share.md`

No shared contract, solution, Directory, Requirements, or other project paths were changed.

## Changes

- Replaced the placeholder clipboard tracker with injected clock/reader/notification contracts, bounded lock retry, quality states, generation tracking, copy/cut intent, repeated-generation paste candidates, and cancellation-safe `ClipboardEventSource`.
- Added a Windows STA hidden message-only window using `AddClipboardFormatListener` and `WM_CLIPBOARDUPDATE`; there is no clipboard polling loop.
- Added native CF_HDROP path extraction, Preferred DropEffect copy/cut reading, and clipboard sequence generation reading without retaining clipboard bytes or file contents.
- Added `NetShareEnum` level-2 startup snapshots and deterministic shared-contract `ShareDescriptor` diffs. Share events use `EventOrigin.ShareChange` and `ShareChanged` hint, with no remote-user or access-history collection.
- Added asynchronous `RegNotifyChangeKeyValue` notification and a 30-second `NetShareEnum` fallback with `FallbackPolling` quality metadata.
- Added local-only cloud placeholder capability detection, hydration/dehydration observation, and metadata-only transition classification without cloud APIs.
- Added SessionAgent versioned IPC: 4-byte little-endian length plus UTF-8 JSON major/minor envelope, 1 MiB frame bound, typed corruption/version failures, pipe streaming, and disconnect recovery.
- Added user-session named-pipe hosting in `Program` and source-mirror documentation for every owned source file/project.

## Important design decisions

The shared `IClipboardEventSource` and `IShareStateSource` contracts are implemented directly; no duplicate shared DTO was introduced. `ShareDescriptor` is reused for snapshots and `ShareChange` only represents a local state transition. Clipboard quality is carried in bounded `SourceEvent.Properties` because the shared `EventQuality` enum does not contain the requirement's four candidate labels.

The production fallback interval is fixed at 30 seconds. A constructor-only interval injection is available to deterministic tests and does not alter production behavior. The native clipboard window runs only in the SessionAgent process, keeping Session 0 from directly reading the user clipboard.

## Tests and results

Command:

```text
dotnet test tests/StorageChronicle.Platform.Windows.Session.Tests/StorageChronicle.Platform.Windows.Session.Tests.csproj --no-restore
```

Result: 17 passed, 0 failed, 0 skipped, 0 warnings/errors.

Build commands:

```text
dotnet build src/StorageChronicle.Platform.Windows.Session/StorageChronicle.Platform.Windows.Session.csproj
dotnet build src/StorageChronicle.SessionAgent/StorageChronicle.SessionAgent.csproj --no-restore
```

Both completed with 0 warnings and 0 errors.

Additional validation:

```text
dotnet build StorageChronicle.slnx
dotnet run --project tools/StorageChronicle.DocMirrorValidator/StorageChronicle.DocMirrorValidator.csproj --no-build -- .
```

Both completed successfully; the solution build reported 0 warnings and 0 errors, and the mirror validator passed.

Coverage includes clipboard generation/copy/cut, repeated paste, lock retry and exhaustion, absent CF_HDROP, cancellation, SID/session guard, share path/permission diffs, registry notification and fallback quality, cloud capability/path loss/hydration transitions, protocol round trip/version/corruption, successful pipe framing, and disconnect recovery.

## Performance and resource behavior

Clipboard notification uses one STA message-pump thread and a channel; it does not poll. Candidate paths are bounded to the event payload and clipboard handles are closed immediately. Share fallback wakes only at the required 30-second interval. SessionAgent reconnects after a client disconnect without retaining a durable clipboard queue.

## Known limitations

- Actual native clipboard, NetShareEnum, registry notification, and Windows attribute behavior require the separate Windows privileged smoke test gate. Ordinary tests use injected fakes to remain deterministic and avoid touching the user's clipboard or changing shares.
- SessionAgent protocol framing is implemented in the owned process boundary. Agent-side connection/authentication integration remains the top-agent/application integration responsibility; `SessionIpcGuard` provides the SID/session/message validation primitive.
- The Cloud Placeholder reader exposes local hydration flags and a transition classifier. It intentionally does not identify a provider or call a cloud API.

## Top-agent review points

- Verify the Agent consumer uses `SessionAgentMessageCodec` and validates `SessionIpcGuard` before accepting clipboard candidates.
- Keep clipboard candidates transient until application correlation confirms a copy; do not append unknown candidates as durable file changes.
- Run the Windows privileged test script for hidden-window notifications, native share enumeration, registry notification, and capability detection.
- Confirm the shared solution already registers both owned projects and the owned test project; no solution edit is included in this branch.

## Shared-contract change request

None. Existing `IClipboardEventSource`, `IShareStateSource`, `ShareDescriptor`, `SourceEvent`, and `ILocalMessageCodec` contracts were reused without modification.
