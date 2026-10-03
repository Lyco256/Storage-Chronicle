# Program.cs

## Role

This read-only console tool compares a real `StorageChronicle.FileMutationWorkload.v2` oracle with the durable Source Event stream, Canonical Event stream, and reconstructed final state in a Storage Chronicle history directory.

## Public types and responsibilities

The entry point validates the oracle schema and per-operation UTC boundaries, reads authoritative segments with `ReadOnlyStorageHistoryReader`, reconstructs final state in memory with `StateEngine`, and emits `StorageChronicle.WindowsTestLabRealIoEvidence.v1`. It never opens SQLite or the normal writer engine, because SQLite WAL access may create shared-memory files and engine startup can repair/recreate the index. Sequence gaps encountered during reconstruction are preserved as explicit failures; a reconstruction-only path permits monotonic events after a gap without changing the recorded event. Acceptance checks use reconstructed parent/name paths rather than leaf names, require every oracle operation to have path-matched canonical evidence, require write/rename/move/delete operation semantics, verify rename/move FileId continuity and delete metadata retention, and compare operations with the final state. A same-name object in another directory, an unrelated operation of the same kind, or a missing final-state object cannot satisfy the oracle.

## Inputs and outputs

`--oracle` points to the workload JSON, `--history` points to the guest-retrieved history directory, and `--output` receives the JSON evidence. The oracle must be a direct child of a marked TestLab root; the output must be a new direct child in that same root, named `real-io-evidence-{RunId}.json`. The run GUID and paired NTFS volume markers must agree. Reparse-point paths, an existing output file or directory, missing markers, and arbitrary output parents fail closed. The tool never creates output directories or replaces an existing target; it uses exclusive create-new semantics. Exit `0` means every check passed; exit `2` means evidence was produced but is ineligible; exit `1` means the comparison could not be executed or safe evidence output was unavailable.

## Dependencies

Depends on the platform-neutral Domain, State, and Storage projects. Its history path is strictly read-only even when the SQLite index is absent or corrupt. Evidence is written only to a separately validated new run-bound file. It does not acquire file contents, calculate content hashes, repair history, or infer process attribution.

## Invariants

Missing oracle operations, absent durable streams, empty final state, invalid timestamps, unsupported process-quality values, read-only observations entering canonical history, path mismatch, incorrect operation semantics, broken rename/move identity, missing delete metadata, or missing final-state coverage remain failures. Directory operations are checked as parent-scoped records rather than descendant event synthesis.

## Threading and lifetime

History enumeration is asynchronous and cancellation-safe through the storage async streams. The opened history is disposed before the tool exits.

## Failure behavior

Malformed input or unavailable history produces a failed artifact only after the run-bound output boundary has been validated; if that new-only destination cannot be created, the error is reported to stderr and no alternate path is written. A partial comparison never receives `AcceptanceEligible=true`.

## Tests

The tool is compiled by the solution build and invoked by the Windows TestLab real-I/O stage. `tests/StorageChronicle.RealIoOracleValidator.Tests` covers exact path/state success, same-name path substitution, wrong rename identity, delete-without-metadata, existing-file preservation, output-root escape, missing root markers, and output-directory collisions.

## OS constraints

The comparison itself is platform-neutral; the workload and Agent history it consumes are produced in the Windows TestLab.

## Change-sensitive contracts

The oracle schema, evidence schema, command-line flags, and operation coverage names are acceptance contracts.
