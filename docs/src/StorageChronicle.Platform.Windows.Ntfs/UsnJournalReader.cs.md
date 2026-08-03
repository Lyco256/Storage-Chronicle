# UsnJournalReader.cs

`UsnJournalReader` performs Journal ID and USN continuity checks before streaming bounded `FSCTL_READ_USN_JOURNAL` batches. It reads only after the persisted USN, stops at the current journal continuation, and stores the latest Journal ID/next USN as `LastObservedState` for the agent. Journal creation, capacity changes, and whole-volume rescans are outside this class.

If the journal is absent, the ID changes, or the persisted USN is below the valid range, the reader emits an explicit gap and stops that pass for recovery. Access denial and media removal become explicit gap results; cancellation is rethrown and prevents the next native read. `UsnReadOptions` bounds buffer size, timeout, and reason mask.

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
