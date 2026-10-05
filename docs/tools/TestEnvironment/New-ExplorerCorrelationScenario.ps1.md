# New-ExplorerCorrelationScenario.ps1

## Role

Prepares an explicitly approved, disposable, marker-verified local fixed NTFS workload volume for human-assisted Explorer correlation testing. It creates only empty files/directories and a bounded ten-row operation plan; it never creates a live acceptance result and never reads or stores target file contents.

## Usage

Use only a dedicated test PC after the disposable test volume has been provisioned and independently reviewed. The legacy VM runner is retired; this script is not a VM setup mechanism:

```powershell
.\New-ExplorerCorrelationScenario.ps1 -Root 'D:\StorageChronicleTestData' -TestId '<run-id>' -Apply
```

The script refuses a volume root, repository/protected user/system/synchronized path, reparse path, absent/mismatched marker, non-fixed/non-NTFS volume, live volume-identity mismatch, or TestId mismatch. Before reading either marker, it requires the root to contain exactly the two regular ownership-marker files; existing user/workload entries make it fail closed. Both markers must bind to the same live volume identity and approved label. With `-Apply`, the selected run directory must be new, and the plan output must be a direct child of that directory; all files use exclusive `CreateNew` semantics and existing files are never overwritten. Without `-Apply`, preflight emits JSON to stdout and exits nonzero without creating any file or directory, so it cannot consume or contaminate the marker-only root. `OutputPath` is accepted only with `-Apply`. The script is not an acceptance authorization and must remain gated by Requirement 37's static audit and user approval.

## Output and acceptance boundary

The output schema is `StorageChronicle.ExplorerCorrelationPlan.v1` with ten operations: file copy, tree copy, same-generation second paste, clipboard-change paste, rename, same-volume move, drag-copy, drag-move, delete, and optional recycle/restore. The plan is preparation evidence only (`AcceptanceEligible=false`). The operator must execute the rows in the real Explorer process, independently record the resulting Explorer scenario, and produce `StorageChronicle.ExplorerScenario.v1` rows with observed correlation classes and source file identifiers before invoking `StorageChronicle.LiveCorrelationValidator`.

## Failure behavior and tests

Marker, volume, path, or apply validation fails closed with exit code `1`; preflight returns exit code `2`; successful preparation returns `0`. Relevant checks are PowerShell parser validation, architecture source-contract tests, and (only after all safety gates) the manually observed correlation procedure. The script has not been run on a physical test volume.
