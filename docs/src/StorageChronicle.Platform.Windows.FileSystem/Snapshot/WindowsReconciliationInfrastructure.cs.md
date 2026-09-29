# WindowsReconciliationInfrastructure.cs

Provides the production metadata reader and bounded Windows-only scopes used by confirmed reconciliation. `WindowsFileMetadataReader` can open a metadata-only handle for either a file or directory so each candidate can receive an I/O-priority hint attempt. `WindowsSeBackupPrivilegeScope` duplicates and impersonates a thread token only for `SeBackupPrivilege`, reports enablement failure without crashing the scan, and always reverts the thread. `WindowsReconciliationPriorityScope` enters and exits thread background I/O mode and exposes optional low file-I/O priority hints without dropping events. No scope is used by normal monitoring, UI, or Session Agent paths.

## Role

This Windows-only boundary supplies production metadata access and explicit best-effort OS scopes for confirmed reconciliation.

## Inputs and outputs

It accepts a path or native metadata handle and returns metadata or telemetry-only failure results. It never exposes file contents or hashes.

## Public types and responsibilities

`WindowsFileMetadataReader` reads standard metadata through the existing native boundary. The two scope types expose explicit acceptance telemetry for privilege and priority behavior while keeping Windows APIs isolated from platform-neutral contracts.

## Dependencies

The implementation depends on the Windows native metadata interop boundary and Win32 token/thread/file-information APIs. Platform-neutral contracts are not changed. Access-denied metadata is surfaced as an explicit ACL fallback signal for the Agent reconciliation summary.

## Invariants

The code never reads file contents or computes content hashes, never enables a write-restoration privilege, and treats failed priority/privilege setup as a quality-visible fallback. Disposal restores thread state and closes native handles.

## Threading and lifetime

Privilege and priority objects are short-lived and disposed on the same synchronous call boundary by the Agent runner. Token handles are closed and thread impersonation is reverted even on native failure.

## Failure behavior

Native failure codes are retained in result records and callers may continue with lower metadata quality. Tests cover scope result and failure behavior through injectable runner seams; privileged execution remains part of the Windows TestLab acceptance matrix.

## Tests

`tests/StorageChronicle.Agent.Tests/ConfirmedReconciliationRunnerTests.cs` exercises the injected metadata boundary and candidate query count; the Windows privileged TestLab exercises actual token and priority capabilities.

## OS constraints

The scopes are Windows-only and return disabled/fallback telemetry elsewhere. No process-wide privilege or normal monitoring path uses them.

## Change-sensitive contracts

Only `SeBackupPrivilege` may be enabled, never `SeRestorePrivilege`; failure must preserve a lower-quality record rather than dropping events; background/low-I/O hints are advisory and must not alter durable history.
