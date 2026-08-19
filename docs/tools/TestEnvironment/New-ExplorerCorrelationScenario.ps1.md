# New-ExplorerCorrelationScenario.ps1

## Role

Prepares the disposable, marker-verified NTFS workload volume for the human-assisted Explorer correlation acceptance. It creates only empty files/directories and a bounded ten-row operation plan; it never creates a live acceptance result and never reads or stores target file contents.

## Usage

Run inside the approved `SC-Test-W11` guest after `Invoke-WindowsTestLab.ps1` has initialized the marked Workload volume:

```powershell
.\New-ExplorerCorrelationScenario.ps1 -Root 'D:\StorageChronicleTestData' -TestId '<run-id>' -Apply
```

The script refuses a volume root, an absent/mismatched marker, a non-NTFS role, or a TestId mismatch. Without `-Apply`, it writes only a `READY_FOR_USER_APPLY` preflight record and exits nonzero.

## Output and acceptance boundary

The output schema is `StorageChronicle.ExplorerCorrelationPlan.v1` with ten operations: file copy, tree copy, same-generation second paste, clipboard-change paste, rename, same-volume move, drag-copy, drag-move, delete, and optional recycle/restore. The plan is preparation evidence only (`AcceptanceEligible=false`). The operator must execute the rows in the real Explorer process, independently record the resulting Explorer scenario, and produce `StorageChronicle.ExplorerScenario.v1` rows with observed correlation classes and source file identifiers before invoking `StorageChronicle.LiveCorrelationValidator`.

## Failure behavior and tests

Marker, path, or apply validation fails closed with exit code `1`; preflight returns exit code `2`; successful preparation returns `0`. PowerShell parser validation and the TestLab/manual correlation procedure are the relevant checks.
