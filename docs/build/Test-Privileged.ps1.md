# Test-Privileged.ps1

## Role

Provides the fail-closed Windows acceptance harness for VHDX/USN/MFT, real Agent reconciliation, directory notifications, ETW, SMB, service, Session Agent, interactive-session, and removable-media capabilities. The v2 manifest also carries the complete required-capability list and writes a per-run evidence directory under `artifacts/acceptance/windows-privileged/<run-id>/`.

Volume discovery first uses `Get-Volume -Path` and falls back to the existing drive root when provider-backed paths such as OneDrive do not support path lookup. The fallback is read-only and does not broaden the validated acceptance directory.

## Public types and responsibilities

The script validates safe disposable targets and both TestLab marker files before any capability runs, prepares only explicitly requested guest-internal VHDX resources using DiskPart plus Windows Storage cmdlets, refuses journal creation or resizing, emits a capability manifest, builds the platform integration and Agent test projects, dispatches `Reconciliation`, `AclDeniedMetadata`, and `NonNtfs` to production Agent acceptance tests, and dispatches the remaining wired capabilities to the platform integration test project. `-NonNtfsRoot` supplies a separately marked non-NTFS disposable volume; the capability loop switches the test root only for that test. `-TestId` can require an exact current TestLab marker run ID. A missing user-approved TestLab root is recorded as `NOT_EXECUTED` with exit code `2`; it is never reported as a runner failure or pass. `-WorkloadOraclePath` and `-AgentHistoryPath` are required for an eligible product result: the harness invokes the real-I/O oracle validator and populates the Oracle, source-event, canonical, final-state, reconciliation, and service payloads only from those real artifacts. Capability-only output remains `NOT_EXECUTED`, carries a product-evidence error, and cannot become eligible.

## Inputs and outputs

Inputs are explicit acceptance paths, an optional `NonNtfsRoot`, an approved `TestLabRoot` (or process `SC_TESTLAB_ROOT`), an explicit Session Agent executable/live Agent pipe for IPC acceptance, `-WorkloadOraclePath`, `-AgentHistoryPath`, capability environment variables, and switches. Outputs include the manifest, log, environment, capability, oracle, source-event, canonical, final-state, reconciliation, confirmed-reconciliation, service, real-I/O validator, errors, and result JSON artifacts under `artifacts/acceptance/windows-privileged/<run-id>/`. The environment artifact records ProductName, DisplayVersion, Build, and x64/x86 architecture so the Windows 10 Stage A composer can reject evidence from the wrong OS. The production Agent reconciliation test writes `confirmed-reconciliation.json` itself through `STORAGE_CHRONICLE_RECONCILIATION_EVIDENCE_PATH`; when it does not run, the harness writes an explicit `NOT_EXECUTED` artifact and never upgrades it.

## Dependencies

Requires Windows, the Windows integration and Agent test projects, and caller-provided disposable VHDX/media/SMB/service/session resources.

## Invariants

The script never uses the normal system drive for destructive preparation, never reads or stores file contents, never creates or resizes a USN journal, and never reports an unexecuted capability as passing. Disposable VHDX creation is allowed only beneath the approved TestLab root and uses the `SC_TEST_VOLUME` marker contract.

## Threading and lifetime

VHDX setup and cleanup are scoped to the script process; the test runner owns test lifetime.

## Failure behavior

Missing prerequisites, unsafe paths, capability setup failure, skipped capabilities, or test failure return a non-zero result and preserve diagnostics.

## Tests

Validated by `tests/StorageChronicle.Platform.Windows.Integration.Tests`, the real reconciliation test in `tests/StorageChronicle.Agent.Tests`, and the wrapper `build/Test-WindowsPrivileged.ps1`. The reconciliation test is skipped unless an elevated non-system NTFS acceptance root is explicitly supplied.

## OS constraints

This is Windows-only and must be run on the required Windows 11 and Windows 10 22H2 acceptance hosts.

## Change-sensitive contracts

Capability names, the shared `build/quality/AcceptanceContracts.ps1` list, fail-closed exit codes, safe-target checks, environment variables, and artifact manifest fields are release contracts. The producer and final gates require all sixteen Windows 11 capabilities exactly once; a self-declared subset cannot become eligible.
