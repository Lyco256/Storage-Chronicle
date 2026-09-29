# ClipboardEventSource.cs

## Role

Converts `WM_CLIPBOARDUPDATE` notifications and one-shot clipboard reads into `SourceEvent` clipboard candidates.

## Inputs and outputs

It consumes `IClipboardNotificationSource` and `IClipboardReader`, retries only `Locked` results according to a bounded policy, and emits properties for generation, copy/cut, quality, path count, and paths. It never emits clipboard contents or a durable copy confirmation.

## Invariants and failure behavior

Every notification produces at most one bounded source event. Read success is `ConfirmedIntent`; empty/unsupported reads are `NotIdentified`; exhausted locks are `SourceUnknown` with `EventQuality.Unknown`. Source sequence values are local to this stream. Cancellation is checked before reads, between retries, and during enumeration.

## Dependencies and tests

Depends on Domain, Contracts, Platform.Abstractions, and the session records. Tests inject notification and reader fakes for generation, copy/cut, lock retry, empty clipboard, and cancellation behavior.

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
