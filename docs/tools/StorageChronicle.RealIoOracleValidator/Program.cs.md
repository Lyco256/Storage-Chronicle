# Program.cs

## Role

This read-only console tool compares a real `StorageChronicle.FileMutationWorkload.v2` oracle with the durable Source Event stream, Canonical Event stream, and reconstructed final state in a Storage Chronicle history directory.

## Public types and responsibilities

The entry point validates the oracle schema and per-operation UTC boundaries, opens the immutable history through `AppendOnlyStorageEngine`, checks source/canonical/state presence, checks compatible high-level operation coverage, verifies process-quality values and excludes read/open/query observations from durable canonical history, and emits `StorageChronicle.WindowsTestLabRealIoEvidence.v1`.

## Inputs and outputs

`--oracle` points to the workload JSON, `--history` points to the guest-retrieved history directory, and `--output` receives the JSON evidence. Exit `0` means every check passed; exit `2` means evidence was produced but is ineligible; exit `1` means the comparison could not be executed.

## Dependencies

Depends on the platform-neutral Domain and Storage projects. It does not acquire file contents, calculate content hashes, mutate history, or infer process attribution.

## Invariants

Missing oracle operations, absent durable streams, empty final state, invalid timestamps, unsupported process-quality values, read-only observations entering canonical history, or missing high-level operation coverage remain failures. Directory operations are checked as parent-scoped records rather than descendant event synthesis.

## Threading and lifetime

History enumeration is asynchronous and cancellation-safe through the storage async streams. The opened history is disposed before the tool exits.

## Failure behavior

Malformed input or unavailable history produces an explicit failed artifact. A partial comparison never receives `AcceptanceEligible=true`.

## Tests

The tool is compiled by the solution build and invoked by the Windows TestLab real-I/O stage.

## OS constraints

The comparison itself is platform-neutral; the workload and Agent history it consumes are produced in the Windows TestLab.

## Change-sensitive contracts

The oracle schema, evidence schema, command-line flags, and operation coverage names are acceptance contracts.
