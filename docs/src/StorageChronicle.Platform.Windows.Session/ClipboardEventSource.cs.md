# ClipboardEventSource.cs

## Role

Converts `WM_CLIPBOARDUPDATE` notifications and one-shot clipboard reads into `SourceEvent` clipboard candidates.

## Inputs and outputs

It consumes `IClipboardNotificationSource` and `IClipboardReader`, retries only `Locked` results according to a bounded policy, and emits properties for generation, copy/cut, quality, path count, and paths. It never emits clipboard contents or a durable copy confirmation.

## Invariants and failure behavior

Every notification produces at most one bounded source event. Read success is `ConfirmedIntent`; empty/unsupported reads are `NotIdentified`; exhausted locks are `SourceUnknown` with `EventQuality.Unknown`. Source sequence values are local to this stream. Cancellation is checked before reads, between retries, and during enumeration.

## Dependencies and tests

Depends on Domain, Contracts, Platform.Abstractions, and the session records. Tests inject notification and reader fakes for generation, copy/cut, lock retry, empty clipboard, and cancellation behavior.
