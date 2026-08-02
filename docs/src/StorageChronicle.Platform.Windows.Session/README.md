# StorageChronicle.Platform.Windows.Session

## Role

Provides the Windows user-session clipboard, local SMB share, and local cloud-placeholder adapters. It runs in the logged-on session boundary and exposes bounded OS-neutral source contracts to the rest of Storage Chronicle.

## Public contract

`ClipboardEventSource` implements the shared `IClipboardEventSource`; `WindowsShareStateSource` implements `IShareStateSource`. `WindowsClipboardNotificationSource` receives `WM_CLIPBOARDUPDATE` through a hidden message-only window. `NetShareSnapshotReader` and `RegistryShareChangeNotifier` isolate `NetShareEnum` and `RegNotifyChangeKeyValue`. `WindowsCloudPlaceholderReader` exposes only local hydration attributes.

## Invariants

- Clipboard monitoring is event-driven; no polling loop reads the clipboard.
- Only CF_HDROP paths, copy/cut intent, and a clipboard generation are retained in bounded source properties.
- Lock retries are short and cancellation-aware; an exhausted lock is `SourceUnknown`, never a confirmed copy.
- Share events use `EventOrigin.ShareChange` and `CanonicalOperation.ShareChanged`, never file metadata operations.
- Registry notification is preferred; only unavailable registration activates the 30-second fallback quality marker.
- No remote users, remote PCs, remote access history, cloud API, cloud history, or clipboard bytes are read.

## Failure and cancellation

Native capability boundaries return `Unsupported` or bounded `Unknown` observations where safe. Win32 access errors remain exceptions for startup snapshot callers. Long-running streams honor cancellation and dispose native notification resources.

## Tests

The owned tests cover generation/copy/cut, repeated paste, lock retry/exhaustion, absent CF_HDROP, cancellation, share diff and permission changes, registry/fallback quality, cloud transitions, IPC round trips/corruption/disconnect recovery, and SID/session guards.
