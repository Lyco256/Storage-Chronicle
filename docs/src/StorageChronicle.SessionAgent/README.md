# StorageChronicle.SessionAgent

## Role

Runs in the logged-on user session, owns the clipboard listener boundary, and streams bounded clipboard candidates to the Agent over a versioned local pipe.

## Public contract

`SessionAgentMessageCodec` frames UTF-8 JSON with a 4-byte little-endian length and protocol major/minor. `SessionAgentPipeServer` streams `ClipboardCandidateMessage` values and returns a disconnect result so the host can reconnect.

## Invariants

The process does not read clipboard state from Session 0. The protocol rejects unsupported majors, malformed lengths, oversized frames, invalid JSON, and missing payloads. Candidate payloads contain paths, generation, copy/cut, quality, and observation time only.

## Failure and recovery

Client stream disconnects become `Disconnected=true` rather than corrupting durable history; `Program` creates a new named-pipe server for the next client. Cancellation propagates for controlled shutdown.

## Tests

The owned session tests cover protocol round trips, major-version rejection, corrupt lengths, stream delivery, and disconnect recovery.

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
