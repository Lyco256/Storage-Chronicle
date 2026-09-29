# WindowsClipboardReader.cs

## Role

Reads local Windows clipboard metadata through `OpenClipboard`, CF_HDROP, `DragQueryFile`, `GetClipboardSequenceNumber`, and Preferred DropEffect.

## Boundary

The reader opens the clipboard only in response to a notification, returns file paths and copy/cut intent, and closes the clipboard immediately. It never calls `GetClipboardData` for file bytes or stores HGLOBAL contents. Access denied is classified as a retryable lock.

## Failure and platform behavior

Non-Windows execution returns `Unsupported`. Missing CF_HDROP returns `Empty`; missing or unreadable drop effect safely means copy/unknown intent rather than cut. Cancellation is checked before opening and while enumerating paths.

## Tests

Native calls are isolated behind `IClipboardReader`; the owned tests use deterministic fakes to cover lock, empty, copy, cut, and quality behavior. Native layout/behavior is intended for the Windows privileged session test gate.
