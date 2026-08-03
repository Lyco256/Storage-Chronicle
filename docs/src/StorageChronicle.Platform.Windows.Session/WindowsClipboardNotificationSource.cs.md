# WindowsClipboardNotificationSource.cs

## Role

Hosts a hidden message-only window on a dedicated STA thread and registers it with `AddClipboardFormatListener`.

## Lifecycle and invariants

The listener emits only `WM_CLIPBOARDUPDATE` notifications through a bounded channel; it never polls. The window and delegate stay alive for the message pump lifetime, and disposal posts `WM_CLOSE`, removes the listener, and joins the thread. Non-Windows construction completes safely without native calls.

## Failure and cancellation

Window registration failures complete the notification stream with the native exception. Enumeration cancellation stops the channel consumer; disposal tears down native resources. The class is used by the user-session process, never by the Session 0 service.

## Tests

Deterministic stream tests inject `IClipboardNotificationSource`; the native window is reserved for Windows privileged smoke testing so ordinary tests do not touch the user's clipboard.

## Role

This mirror documents the source boundary for this file and explains how it participates in Storage Chronicle.

## Public types and responsibilities

Public types preserve source facts and the explicitly owned responsibility; UI interpretation and correlation remain outside this boundary.

## Inputs and outputs

Inputs and outputs are the declared contracts of the source file. File contents and file-content hashes are never an input or output.

## Dependencies

Dependencies are limited to the referenced project contracts and platform services shown by the source file.

## Invariants

Clipboard notifications use a bounded transient correlation buffer with oldest-entry eviction; clipboard candidates are not durable file history until a confirmed file operation is observed. The source keeps canonical facts distinguishable from reconstructed state and does not synthesize descendant events.

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
