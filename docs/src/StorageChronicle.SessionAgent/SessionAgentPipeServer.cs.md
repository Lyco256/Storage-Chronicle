# SessionAgentPipeServer.cs

## Role

Streams `IClipboardEventSource` values to an output stream as versioned `ClipboardCandidate` frames.

## Inputs and outputs

The server consumes Domain `SourceEvent` values and maps bounded clipboard properties into `ClipboardCandidateMessage`. It writes one length-prefixed frame per event, flushes it, and reports sent count plus disconnect status.

## Failure, cancellation, and recovery

Cancellation is propagated. IOException and ObjectDisposedException from a disconnected client produce `Disconnected=true` so the host can reopen a named pipe. The server does not persist, normalize, or confirm copy operations.

## Tests

The owned tests use a memory stream for successful framing and a disconnecting stream for recovery behavior.

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
