# Program.cs

## Role

Starts the logged-on Session Agent, creates the hidden event-driven clipboard listener, and forwards only bounded clipboard metadata to the Agent's versioned named pipe. Each connection identifies itself as the Session Agent role before sending its ClipboardCandidate request; the Agent verifies the session and published executable role. The explicit `--once` acceptance mode exits after one real clipboard notification has been forwarded, which lets the privileged matrix prove a live IPC round trip without fabricating a candidate.

## Boundary and failure behavior

The Session Agent never transports clipboard bytes. It maps path candidates, generation, quality, and source sequence to `ClipboardCandidateRequest`, reconnects per bounded message, reads the response frame, and honors cancellation.

## Tests

Protocol and clipboard source tests cover malformed frames, cancellation, clipboard lock retry, disconnect, and source mapping. Real clipboard smoke tests remain in the privileged Windows category.

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
