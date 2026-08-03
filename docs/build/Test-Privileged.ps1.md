# Test-Privileged.ps1

## Role

Provides the fail-closed Windows acceptance harness for VHDX/USN/MFT, directory notifications, ETW, SMB, service, interactive-session, and removable-media capabilities.

Volume discovery first uses `Get-Volume -Path` and falls back to the existing drive root when provider-backed paths such as OneDrive do not support path lookup. The fallback is read-only and does not broaden the validated acceptance directory.

## Public types and responsibilities

The script validates safe disposable targets, prepares only explicitly requested VHDX/USN resources, emits a capability manifest, runs the isolated Windows integration project with the xUnit capability-trait filter passed to the test runner, and returns a non-zero status when required capabilities were not executed.

## Inputs and outputs

Inputs are explicit acceptance paths, capability environment variables, and switches. Outputs are ignored capability logs/manifests under `artifacts/`.

## Dependencies

Requires Windows, the Windows integration test project, and caller-provided disposable VHDX/media/SMB/service/session resources.

## Invariants

The script never uses the normal system drive for destructive preparation, never reads or stores file contents, and never reports an unexecuted capability as passing.

## Threading and lifetime

VHDX setup and cleanup are scoped to the script process; the test runner owns test lifetime.

## Failure behavior

Missing prerequisites, unsafe paths, capability setup failure, skipped capabilities, or test failure return a non-zero result and preserve diagnostics.

## Tests

Validated by `tests/StorageChronicle.Platform.Windows.Integration.Tests` and the wrapper `build/Test-WindowsPrivileged.ps1`.

## OS constraints

This is Windows-only and must be run on the required Windows 11 and Windows 10 22H2 acceptance hosts.

## Change-sensitive contracts

Capability names, fail-closed exit codes, safe-target checks, environment variables, and artifact manifest fields are release contracts.
