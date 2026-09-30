# WindowsEtwFileIoCollector.cs

This file contains the Windows ETW File I/O collector. It runs a TraceEvent kernel session, translates create/write/delete/rename metadata and process attribution into platform-neutral source facts, and retains read/query/directory-enumeration observations only in the bounded transient correlation channel. ProcessStop removes the PID mapping and reports only the matched process-instance exit time to the Agent's in-memory lifecycle sink; no durable file/source event is created for process exit.

The collector has a bounded queue. An ETW queue overflow stops the session and emits an unverified gap after draining the bounded queue; startup and processing failures propagate to the caller, which can record a gap and recover through reconciliation. Read/query/directory-enumeration observations remain transient in the Agent pipeline and are not durable source or canonical history. A narrow internal session/factory boundary isolates TraceEvent APIs and enables deterministic fake-session tests without starting a host ETW session. Tests cover operation/observation translation, process attribution and exit, bounded overflow, cancellation/disposal, and startup failure. A live host ETW capability test remains an explicit unrun privileged check. The collector never reads file contents or content hashes and emits no synthetic descendant event for directory operations.

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
