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

## Role

This mirror documents the source boundary for this file and explains how it participates in Storage Chronicle.

## Public types and responsibilities

Public types preserve source facts and the explicitly owned responsibility; UI interpretation and correlation remain outside this boundary.

## Inputs and outputs

Inputs and outputs are the declared contracts of the source file. File contents and file-content hashes are never an input or output.

## Dependencies

Dependencies are limited to the referenced project contracts and platform services shown by the source file.

## Invariants

The source keeps canonical facts distinguishable from reconstructed state and does not synthesize descendant events.

## Threading and lifetime

Callers own cancellation and lifetime; asynchronous work must not outlive the owning pipeline or UI scope.

## Failure behavior

Failure, corruption, cancellation, and recovery remain observable and are not converted into a false successful observation.

## Tests

Validated by tests/StorageChronicle.Integration.Tests and the affected integration tests.

## OS constraints

Platform-neutral behavior remains portable; Windows-only APIs are isolated in the Windows platform projects.

## Change-sensitive contracts

Public names, serialized fields, persistence boundaries, and the mirrored path are compatibility-sensitive contracts.
