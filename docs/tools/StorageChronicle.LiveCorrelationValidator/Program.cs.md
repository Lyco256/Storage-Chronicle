# `tools/StorageChronicle.LiveCorrelationValidator/Program.cs`

## Role

This executable is the fail-closed producer for the real Agent/process/Explorer correlation acceptance artifact. It consumes a workload oracle, durable Agent history, a separately recorded Explorer scenario, and the TestLab environment manifest. It never reads target file contents or content hashes.

## Public types and entry point

`StorageChronicle.LiveCorrelationValidator.Program.Main` accepts `--oracle`, `--history`, `--explorer`, `--environment`, and `--output`. It returns zero only when the evidence is a non-diagnostic Windows 11 `SC-Test-W11-VBox` TestLab run with complete workload state, zero false exact process attributions, and zero false Explorer source attributions.

## Invariants

- Fixture or diagnostic artifacts cannot be marked acceptance-eligible.
- The environment paths must identify the same oracle, history, and Explorer evidence supplied to the command.
- Process attribution is exact only when the durable process instance identifier matches a PID/start-time pair in the real workload process collection.
- Explorer correlation requires an actual Explorer process event and a correlated source file identifier; when the scenario supplies an expected source identifier, the durable identifier must match it exactly. Unknown or contradictory source claims remain failures.
- Explorer scenario expectations accept the repository's `Correlated`/`Uncorrelated`/`ExcludedNonExplorer` vocabulary and normalize it to the evidence counters `SourceCorrelated`, `SourceUnknown`, and `NotIdentified`.
- Workload operations must have matching canonical evidence and final state evidence, including a live or virtual-deleted final state as appropriate. Missing rows are reported as dropped/missing rather than synthesized.
- Evidence uses metadata, identifiers, timestamps, and bounded properties only; it does not open or hash target files.

## Dependencies and failure behavior

The validator depends on `StorageChronicle.Domain` contracts and `StorageChronicle.Storage` durable history readers. Missing, malformed, mismatched, diagnostic, or incomplete inputs produce a `FAILED` artifact and exit code `2`.

## Relevant tests

`tests/StorageChronicle.LiveCorrelationValidator.Tests/LiveCorrelationValidatorTests.cs` covers the real-shaped pass path and fail-closed environment/path and attribution failures. The repository final acceptance scripts additionally validate the serialized schema, row/count consistency, TestLab identity, and artifact existence.
