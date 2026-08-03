# Program.cs

Creates the LocalSystem-compatible Windows Service host. It composes durable append storage, settings persistence/history, projection IPC, NTFS USN/MFT, the production initial metadata snapshot reader, scoped directory monitoring, session clipboard, and share collectors, and applies the service recovery action delays at startup. NTFS is delegated away from the directory collector only for whole-volume scopes; configured NTFS subdirectories use the scoped directory collector to avoid leaking unmonitored events. File contents and clipboard contents are never retained.

## Role

This mirror documents the source boundary for this file and explains how it participates in Storage Chronicle.

## Public types and responsibilities

Public types preserve source facts and the explicitly owned responsibility; UI interpretation and correlation remain outside this boundary.

## Inputs and outputs

Inputs and outputs are the declared contracts of the source file. File contents and file-content hashes are never an input or output.

## Dependencies

Dependencies are limited to the referenced project contracts and platform services shown by the source file.

## Invariants

The service host composes platform collectors with the platform-neutral pipeline, stores data below ProgramData by default, and constrains the effective startup flush interval to 1 through 60 seconds. The source keeps canonical facts distinguishable from reconstructed state and does not synthesize descendant events.

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
