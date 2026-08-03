# WindowsEtwFileIoCollector.cs

This file contains the Windows ETW File I/O collector. It runs a TraceEvent kernel session, translates create/write/delete/rename metadata and process attribution into platform-neutral source facts, and retains read/query/directory-enumeration observations only in the bounded transient correlation channel.

The collector has a bounded queue. An ETW queue overflow or session failure is surfaced as a collector failure so the Agent can record an unverified gap and recover through reconciliation. It never reads file contents or content hashes, emits no synthetic descendant event for directory operations, and keeps Windows ETW APIs isolated from the platform-neutral contracts. Tests exercise translation, filtering, cancellation, overflow, and unavailable-ETW failure paths.

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
