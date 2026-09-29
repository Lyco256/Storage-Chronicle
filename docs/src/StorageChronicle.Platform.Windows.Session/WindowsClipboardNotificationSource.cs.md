# WindowsClipboardNotificationSource.cs

## Role

Hosts a hidden message-only window on a dedicated STA thread and registers it with `AddClipboardFormatListener`.

## Lifecycle and invariants

The listener emits only `WM_CLIPBOARDUPDATE` notifications through a bounded channel; it never polls. The window and delegate stay alive for the message pump lifetime, and disposal posts `WM_CLOSE`, removes the listener, and joins the thread. Non-Windows construction completes safely without native calls.

## Failure and cancellation

Window registration failures complete the notification stream with the native exception. Enumeration cancellation stops the channel consumer; disposal tears down native resources. The class is used by the user-session process, never by the Session 0 service.

## Tests

Deterministic stream tests inject `IClipboardNotificationSource`; the native window is reserved for Windows privileged smoke testing so ordinary tests do not touch the user's clipboard.
