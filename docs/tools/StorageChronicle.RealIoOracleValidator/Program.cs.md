# Program.cs

## Role

This read-only console tool compares a real `StorageChronicle.FileMutationWorkload.v2` oracle with the durable Source Event stream, Canonical Event stream, and reconstructed final state in a Storage Chronicle history directory.

## Public types and responsibilities

The entry point validates the oracle schema and per-operation UTC boundaries, opens the immutable history through `AppendOnlyStorageEngine`, and emits `StorageChronicle.WindowsTestLabRealIoEvidence.v1`. Acceptance checks use reconstructed parent/name paths rather than leaf names, require every oracle operation to have path-matched canonical evidence, require write/rename/move/delete operation semantics, verify rename/move FileId continuity and delete metadata retention, and compare operations with the final state. A same-name object in another directory, an unrelated operation of the same kind, or a missing final-state object cannot satisfy the oracle.

## Inputs and outputs

`--oracle` points to the workload JSON, `--history` points to the guest-retrieved history directory, and `--output` receives the JSON evidence. Exit `0` means every check passed; exit `2` means evidence was produced but is ineligible; exit `1` means the comparison could not be executed.

## Dependencies

Depends on the platform-neutral Domain and Storage projects. It does not acquire file contents, calculate content hashes, mutate history, or infer process attribution.

## Invariants

Missing oracle operations, absent durable streams, empty final state, invalid timestamps, unsupported process-quality values, read-only observations entering canonical history, path mismatch, incorrect operation semantics, broken rename/move identity, missing delete metadata, or missing final-state coverage remain failures. Directory operations are checked as parent-scoped records rather than descendant event synthesis.

## Threading and lifetime

History enumeration is asynchronous and cancellation-safe through the storage async streams. The opened history is disposed before the tool exits.

## Failure behavior

Malformed input or unavailable history produces an explicit failed artifact. A partial comparison never receives `AcceptanceEligible=true`.

## Tests

The tool is compiled by the solution build and invoked by the Windows TestLab real-I/O stage. `tests/StorageChronicle.RealIoOracleValidator.Tests` covers exact path/state success and rejects same-name path substitution, wrong rename identity, and delete-without-metadata artifacts.

## OS constraints

The comparison itself is platform-neutral; the workload and Agent history it consumes are produced in the Windows TestLab.

## Change-sensitive contracts

The oracle schema, evidence schema, command-line flags, and operation coverage names are acceptance contracts.
